"""Minimal, fast PPO for GPU-vectorised MuJoCo Warp environments (rsl_rl-style).

- Gaussian MLP actor-critic with state-independent log-std.
- Running observation normalisation, baked into the exported ONNX graph.
- GAE with time-out bootstrapping, clipped value loss, adaptive learning rate on KL.
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Dict, Tuple

import numpy as np
import torch
import torch.nn as nn


class RunningMeanStd:
    def __init__(self, shape: int, device: str, eps: float = 1e-4):
        self.mean = torch.zeros(shape, device=device)
        self.var = torch.ones(shape, device=device)
        self.count = eps

    @torch.no_grad()
    def update(self, x: torch.Tensor) -> None:
        batch_mean = x.mean(0)
        batch_var = x.var(0, unbiased=False)
        batch_count = x.shape[0]
        delta = batch_mean - self.mean
        tot = self.count + batch_count
        new_mean = self.mean + delta * batch_count / tot
        m_a = self.var * self.count
        m_b = batch_var * batch_count
        m2 = m_a + m_b + delta.pow(2) * self.count * batch_count / tot
        self.mean, self.var, self.count = new_mean, m2 / tot, tot

    def normalize(self, x: torch.Tensor, clip: float = 10.0) -> torch.Tensor:
        return torch.clamp((x - self.mean) / torch.sqrt(self.var + 1e-8), -clip, clip)

    def state_dict(self) -> Dict[str, torch.Tensor]:
        return {"mean": self.mean, "var": self.var, "count": torch.tensor(self.count)}

    def load_state_dict(self, sd: Dict[str, torch.Tensor]) -> None:
        self.mean = sd["mean"].to(self.mean.device)
        self.var = sd["var"].to(self.var.device)
        self.count = float(sd["count"])


def mlp(inp: int, hidden: Tuple[int, ...], out: int) -> nn.Sequential:
    layers = []
    last = inp
    for h in hidden:
        layers += [nn.Linear(last, h), nn.ELU()]
        last = h
    layers.append(nn.Linear(last, out))
    return nn.Sequential(*layers)


class ActorCritic(nn.Module):
    def __init__(self, obs_dim: int, act_dim: int, hidden=(512, 256, 128), init_std: float = 0.8):
        super().__init__()
        self.actor = mlp(obs_dim, hidden, act_dim)
        self.critic = mlp(obs_dim, hidden, 1)
        self.log_std = nn.Parameter(torch.full((act_dim,), float(np.log(init_std))))
        for m in self.modules():
            if isinstance(m, nn.Linear):
                nn.init.orthogonal_(m.weight, gain=np.sqrt(2))
                nn.init.zeros_(m.bias)
        nn.init.orthogonal_(self.actor[-1].weight, gain=0.01)
        nn.init.orthogonal_(self.critic[-1].weight, gain=1.0)

    def dist(self, obs: torch.Tensor) -> torch.distributions.Normal:
        mean = self.actor(obs)
        std = self.log_std.clamp(-5.0, 1.0).exp().expand_as(mean)
        return torch.distributions.Normal(mean, std)

    @torch.no_grad()
    def act(self, obs: torch.Tensor):
        d = self.dist(obs)
        a = d.sample()
        return a, d.log_prob(a).sum(-1), self.critic(obs).squeeze(-1), d.mean, d.stddev

    def evaluate(self, obs: torch.Tensor, actions: torch.Tensor):
        d = self.dist(obs)
        return d.log_prob(actions).sum(-1), d.entropy().sum(-1), self.critic(obs).squeeze(-1), d.mean, d.stddev


@dataclass
class PPOConfig:
    steps_per_env: int = 24
    epochs: int = 5
    minibatches: int = 4
    gamma: float = 0.99
    lam: float = 0.95
    clip: float = 0.2
    entropy_coef: float = 0.005
    value_coef: float = 1.0
    lr: float = 1e-3
    desired_kl: float = 0.01
    max_grad_norm: float = 1.0
    obs_clip: float = 10.0


class PPO:
    def __init__(self, obs_dim: int, act_dim: int, num_envs: int, device: str, cfg: PPOConfig):
        self.cfg = cfg
        self.device = device
        self.num_envs = num_envs
        self.model = ActorCritic(obs_dim, act_dim).to(device)
        self.obs_rms = RunningMeanStd(obs_dim, device)
        self.opt = torch.optim.Adam(self.model.parameters(), lr=cfg.lr)
        T, N = cfg.steps_per_env, num_envs
        self.buf = {
            "obs": torch.zeros(T, N, obs_dim, device=device),
            "act": torch.zeros(T, N, act_dim, device=device),
            "logp": torch.zeros(T, N, device=device),
            "rew": torch.zeros(T, N, device=device),
            "done": torch.zeros(T, N, device=device),
            "timeout": torch.zeros(T, N, device=device),
            "val": torch.zeros(T, N, device=device),
            "mu": torch.zeros(T, N, act_dim, device=device),
            "sigma": torch.zeros(T, N, act_dim, device=device),
        }
        self.t = 0

    # ---- rollout -------------------------------------------------------------------------------
    def act(self, raw_obs: torch.Tensor) -> torch.Tensor:
        self.obs_rms.update(raw_obs)
        obs = self.obs_rms.normalize(raw_obs, self.cfg.obs_clip)
        a, logp, v, mu, sigma = self.model.act(obs)
        b = self.buf
        b["obs"][self.t] = obs
        b["act"][self.t] = a
        b["logp"][self.t] = logp
        b["val"][self.t] = v
        b["mu"][self.t] = mu
        b["sigma"][self.t] = sigma
        return a

    def record(self, rew: torch.Tensor, done: torch.Tensor, timeout: torch.Tensor) -> None:
        b = self.buf
        b["rew"][self.t] = rew
        b["done"][self.t] = done.float()
        b["timeout"][self.t] = timeout.float()
        self.t += 1

    # ---- update --------------------------------------------------------------------------------
    @torch.no_grad()
    def _gae(self, last_val: torch.Tensor):
        b, c = self.buf, self.cfg
        T = c.steps_per_env
        adv = torch.zeros_like(b["rew"])
        rew = b["rew"] + c.gamma * b["val"] * b["timeout"]   # bootstrap through time-outs
        gae = torch.zeros(self.num_envs, device=self.device)
        for t in reversed(range(T)):
            next_val = last_val if t == T - 1 else b["val"][t + 1]
            nonterm = 1.0 - b["done"][t]
            delta = rew[t] + c.gamma * next_val * nonterm - b["val"][t]
            gae = delta + c.gamma * c.lam * nonterm * gae
            adv[t] = gae
        ret = adv + b["val"]
        return adv, ret

    def update(self, last_raw_obs: torch.Tensor) -> Dict[str, float]:
        c, b = self.cfg, self.buf
        with torch.no_grad():
            last_val = self.model.critic(self.obs_rms.normalize(last_raw_obs, c.obs_clip)).squeeze(-1)
        adv, ret = self._gae(last_val)
        adv_n = (adv - adv.mean()) / (adv.std() + 1e-8)

        T, N = c.steps_per_env, self.num_envs
        flat = lambda x: x.reshape(T * N, *x.shape[2:])
        obs, act, logp_old, val_old = flat(b["obs"]), flat(b["act"]), flat(b["logp"]), flat(b["val"])
        mu_old, sigma_old = flat(b["mu"]), flat(b["sigma"])
        adv_f, ret_f = flat(adv_n), flat(ret)

        stats = {"policy_loss": 0.0, "value_loss": 0.0, "entropy": 0.0, "kl": 0.0}
        n_updates = 0
        mb = T * N // c.minibatches
        for _ in range(c.epochs):
            perm = torch.randperm(T * N, device=self.device)
            for i in range(c.minibatches):
                idx = perm[i * mb:(i + 1) * mb]
                logp, ent, v, mu, sigma = self.model.evaluate(obs[idx], act[idx])
                with torch.no_grad():
                    kl = torch.sum(torch.log(sigma / sigma_old[idx] + 1e-5)
                                   + (sigma_old[idx].pow(2) + (mu_old[idx] - mu).pow(2)) / (2.0 * sigma.pow(2)) - 0.5, dim=-1).mean()
                    if kl > c.desired_kl * 2.0:
                        self.cfg.lr = max(1e-5, self.cfg.lr / 1.5)
                    elif kl < c.desired_kl / 2.0 and kl > 0.0:
                        self.cfg.lr = min(1e-2, self.cfg.lr * 1.5)
                    for g in self.opt.param_groups:
                        g["lr"] = self.cfg.lr
                ratio = torch.exp(logp - logp_old[idx])
                surr = -torch.min(ratio * adv_f[idx], ratio.clamp(1 - c.clip, 1 + c.clip) * adv_f[idx]).mean()
                v_clipped = val_old[idx] + (v - val_old[idx]).clamp(-c.clip, c.clip)
                v_loss = torch.max((v - ret_f[idx]).pow(2), (v_clipped - ret_f[idx]).pow(2)).mean()
                loss = surr + c.value_coef * v_loss - c.entropy_coef * ent.mean()
                self.opt.zero_grad(set_to_none=True)
                loss.backward()
                nn.utils.clip_grad_norm_(self.model.parameters(), c.max_grad_norm)
                self.opt.step()
                stats["policy_loss"] += surr.item(); stats["value_loss"] += v_loss.item()
                stats["entropy"] += ent.mean().item(); stats["kl"] += kl.item()
                n_updates += 1
        self.t = 0
        for k in stats: stats[k] /= max(1, n_updates)
        stats["lr"] = self.cfg.lr
        stats["action_std"] = self.model.log_std.exp().mean().item()
        return stats

    # ---- persistence ---------------------------------------------------------------------------
    def save(self, path: str, extra: Dict | None = None) -> None:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        torch.save({"model": self.model.state_dict(), "obs_rms": self.obs_rms.state_dict(),
                    "opt": self.opt.state_dict(), "extra": extra or {}}, path)

    def load(self, path: str) -> Dict:
        ck = torch.load(path, map_location=self.device)
        self.model.load_state_dict(ck["model"])
        self.obs_rms.load_state_dict(ck["obs_rms"])
        try:
            self.opt.load_state_dict(ck["opt"])
        except Exception:
            pass
        return ck.get("extra", {})


class ExportPolicy(nn.Module):
    """obs (1, N) raw -> normalised -> actor mean. Matches Unity's PolicyRunner contract."""

    def __init__(self, model: ActorCritic, rms: RunningMeanStd, clip: float):
        super().__init__()
        self.actor = model.actor
        self.register_buffer("mean", rms.mean.clone())
        self.register_buffer("std", torch.sqrt(rms.var + 1e-8).clone())
        self.clip = clip

    def forward(self, obs: torch.Tensor) -> torch.Tensor:
        x = torch.clamp((obs - self.mean) / self.std, -self.clip, self.clip)
        return self.actor(x)


def export_onnx(ppo: PPO, path: str, obs_dim: int) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wrapper = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip).to("cpu").eval()
    dummy = torch.zeros(1, obs_dim)
    torch.onnx.export(wrapper, dummy, path, input_names=["obs"], output_names=["actions"],
                      opset_version=17, dynamo=False,
                      dynamic_axes={"obs": {0: "batch"}, "actions": {0: "batch"}})
    ppo.model.to(ppo.device)
