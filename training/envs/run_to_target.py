"""GPU-vectorised "run to target" environment on MuJoCo Warp (sync-free step path).

Observation (matches Unity ObservationBuilder / PolicyConfig, external Z-up body frame):
    base_lin_vel(3) base_ang_vel(3) projected_gravity(3) target_command(3)
    joint_pos - default(N) joint_vel(N) last_action(N)
target_command = (unit direction to target in the base-yaw frame (x, y), min(dist, 10) / 10).
Action: PD joint targets, target = default + action * action_scale (radians).
"""
from __future__ import annotations

import json
import math
import os
from typing import Dict, Tuple

import mujoco
import mujoco_warp as mjw
import numpy as np
import torch
import warp as wp


def quat_rotate_inverse(q: torch.Tensor, v: torch.Tensor) -> torch.Tensor:
    """q: (N,4) wxyz, v: (N,3) world -> body."""
    w, xyz = q[:, :1], q[:, 1:]
    t = 2.0 * torch.cross(xyz, v, dim=-1)
    return v - w * t + torch.cross(xyz, t, dim=-1)


def quat_yaw(q: torch.Tensor) -> torch.Tensor:
    w, x, y, z = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    return torch.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def yaw_quat(yaw: torch.Tensor) -> torch.Tensor:
    z = torch.zeros_like(yaw)
    return torch.stack([torch.cos(yaw * 0.5), z, z, torch.sin(yaw * 0.5)], dim=-1)


class RunToTargetEnv:
    def __init__(self, xml_path: str, num_envs: int, device: str = "cuda", seed: int = 0,
                 control_decimation: int = 4, episode_len_s: float = 20.0, action_scale: float = 0.5,
                 target_speed: float = 3.5, target_dist: Tuple[float, float] = (6.0, 16.0),
                 nconmax: int = 24, njmax: int = 96, verbose: bool = False):
        wp.init()
        wp.config.verbose_warnings = verbose
        self.device = device
        self.N = num_envs
        self.decimation = control_decimation
        self.action_scale = action_scale
        self.target_speed = target_speed
        self.target_dist = target_dist
        self.rng = torch.Generator(device=device); self.rng.manual_seed(seed)

        self.m = mujoco.MjModel.from_xml_path(xml_path)
        self.dt = float(self.m.opt.timestep) * control_decimation
        self.max_steps = int(episode_len_s / self.dt)
        d0 = mujoco.MjData(self.m)
        mujoco.mj_resetDataKeyframe(self.m, d0, 0)
        mujoco.mj_forward(self.m, d0)
        self.mw = mjw.put_model(self.m)
        self.mw.opt.warn_overflow = verbose   # device printf warnings cost ~10x throughput on fallen bodies
        self.dw = mjw.put_data(self.m, d0, nworld=num_envs, nconmax=nconmax, njmax=njmax)

        cfg_path = os.path.splitext(xml_path)[0] + "_policy_config.json"
        with open(cfg_path, "r", encoding="utf-8") as f:
            self.cfg = json.load(f)
        self.A = self.m.nu
        self.obs_dim = 12 + 3 * self.A

        # torch views into warp data (no copies)
        self.qpos = wp.to_torch(self.dw.qpos)
        self.qvel = wp.to_torch(self.dw.qvel)
        self.ctrl = wp.to_torch(self.dw.ctrl)
        self.xquat = wp.to_torch(self.dw.xquat)     # (N, nbody, 4) wxyz
        self.xpos = wp.to_torch(self.dw.xpos)       # (N, nbody, 3)
        self.pelvis = mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")

        key = torch.tensor(self.m.key_qpos[0], device=device, dtype=torch.float32)
        self.default_qpos = key
        self.default_joint = key[7:].clone()
        self.stand_height = float(key[2])
        rng = torch.tensor(self.m.jnt_range[1:], device=device, dtype=torch.float32)
        self.joint_lo, self.joint_hi = rng[:, 0], rng[:, 1]
        self.ctrl_lo = torch.tensor(self.m.actuator_ctrlrange[:, 0], device=device, dtype=torch.float32)
        self.ctrl_hi = torch.tensor(self.m.actuator_ctrlrange[:, 1], device=device, dtype=torch.float32)

        N = num_envs
        self.targets = torch.zeros(N, 2, device=device)
        self.last_action = torch.zeros(N, self.A, device=device)
        self.prev_action = torch.zeros(N, self.A, device=device)
        self.step_count = torch.zeros(N, device=device)
        self.episode_return = torch.zeros(N, device=device)
        self.episode_len = torch.zeros(N, device=device)
        self.gravity_world = torch.tensor([0.0, 0.0, -1.0], device=device).expand(N, 3)
        self._acc = {k: torch.zeros((), device=device) for k in
                     ("ret_sum", "len_sum", "fell_sum", "done_n", "v_sum", "reach_sum", "upright_sum", "steps")}
        self.reset()

    # ---- helpers -------------------------------------------------------------------------------
    def _random_targets(self, pos_xy: torch.Tensor) -> torch.Tensor:
        n = pos_xy.shape[0]
        ang = torch.rand(n, generator=self.rng, device=self.device) * 2 * math.pi
        dist = self.target_dist[0] + torch.rand(n, generator=self.rng, device=self.device) * (self.target_dist[1] - self.target_dist[0])
        return pos_xy + torch.stack([torch.cos(ang), torch.sin(ang)], -1) * dist[:, None]

    def _reset_states(self) -> Tuple[torch.Tensor, torch.Tensor]:
        """Fresh (qpos, qvel) candidates for every env; applied with torch.where by the caller."""
        N = self.N
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)
        yaw = (torch.rand(N, generator=self.rng, device=self.device) * 2 - 1) * math.pi
        q[:, 3:7] = yaw_quat(yaw)
        q[:, 2] += 0.02
        q[:, 7:] += (torch.rand(N, self.A, generator=self.rng, device=self.device) * 2 - 1) * 0.05
        v = torch.zeros(N, self.qvel.shape[1], device=self.device)
        v[:, :2] = (torch.rand(N, 2, generator=self.rng, device=self.device) * 2 - 1) * 0.2
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        """mask: (N,) bool. Sync-free masked reset."""
        q, v = self._reset_states()
        m1 = mask[:, None]
        self.qpos.copy_(torch.where(m1, q, self.qpos))
        self.qvel.copy_(torch.where(m1, v, self.qvel))
        self.ctrl.copy_(torch.where(m1, q[:, 7:], self.ctrl))
        zero_a = torch.zeros_like(self.last_action)
        self.last_action = torch.where(m1, zero_a, self.last_action)
        self.prev_action = torch.where(m1, zero_a, self.prev_action)
        self.step_count = torch.where(mask, torch.zeros_like(self.step_count), self.step_count)
        self.episode_return = torch.where(mask, torch.zeros_like(self.episode_return), self.episode_return)
        self.episode_len = torch.where(mask, torch.zeros_like(self.episode_len), self.episode_len)
        self.targets = torch.where(m1, self._random_targets(q[:, :2]), self.targets)

    def reset(self) -> torch.Tensor:
        self._apply_reset(torch.ones(self.N, dtype=torch.bool, device=self.device))
        mjw.forward(self.mw, self.dw)
        self._obs = self.compute_obs()
        return self._obs

    def _base_state(self):
        q = self.xquat[:, self.pelvis]                    # (N,4) wxyz
        pos = self.xpos[:, self.pelvis]
        lin_w = self.qvel[:, :3]
        ang_b = self.qvel[:, 3:6]                          # free joint: angular velocity in body frame
        lin_b = quat_rotate_inverse(q, lin_w)
        grav_b = quat_rotate_inverse(q, self.gravity_world)
        return q, pos, lin_w, lin_b, ang_b, grav_b

    def compute_obs(self) -> torch.Tensor:
        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        yaw = quat_yaw(q)
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1, keepdim=True)
        c, s = torch.cos(yaw), torch.sin(yaw)
        dx = c * to_t[:, 0] + s * to_t[:, 1]
        dy = -s * to_t[:, 0] + c * to_t[:, 1]
        dirn = torch.stack([dx, dy], -1) / dist.clamp_min(1e-3)
        cmd = torch.cat([dirn, (dist.clamp_max(10.0) / 10.0)], -1)
        obs = torch.cat([lin_b, ang_b, grav_b, cmd,
                         self.qpos[:, 7:] - self.default_joint,
                         self.qvel[:, 6:],
                         self.last_action], dim=-1)
        return torch.nan_to_num(obs).clamp(-100.0, 100.0)

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: torch.Tensor):
        action = action.clamp(-5.0, 5.0)
        self.prev_action = self.last_action
        self.last_action = action
        target = (self.default_joint + action * self.action_scale).clamp(self.ctrl_lo, self.ctrl_hi)
        self.ctrl.copy_(target)
        for _ in range(self.decimation):
            mjw.step(self.mw, self.dw)
        self.step_count = self.step_count + 1.0

        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1)
        dir_w = to_t / dist.clamp_min(1e-3)[:, None]
        v_toward = (lin_w[:, :2] * dir_w).sum(-1)
        upright = -grav_b[:, 2]                               # 1 when standing
        yaw = quat_yaw(q)
        fwd_w = torch.stack([torch.cos(yaw), torch.sin(yaw)], -1)
        heading = (fwd_w * dir_w).sum(-1)

        # ---- reward ----
        r_track = 1.5 * torch.exp(-((v_toward - self.target_speed) ** 2) / 2.0)
        r_prog = 0.25 * v_toward.clamp(-1.0, self.target_speed)
        r_alive = 0.3
        r_upright = 0.3 * upright.clamp_min(0.0)
        r_heading = 0.2 * heading
        r_height = -0.5 * (self.stand_height - 0.05 - pos[:, 2]).clamp_min(0.0)
        r_lin_z = -0.5 * lin_b[:, 2] ** 2
        r_ang = -0.03 * (ang_b[:, :2] ** 2).sum(-1)
        r_act = -0.002 * (action ** 2).sum(-1)
        r_rate = -0.02 * ((action - self.prev_action) ** 2).sum(-1)
        jp = self.qpos[:, 7:]
        r_limit = -1.0 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        reached = dist < 0.6
        r_reach = 5.0 * reached.float()
        reward = (r_track + r_prog + r_alive + r_upright + r_heading + r_height + r_lin_z + r_ang
                  + r_act + r_rate + r_limit + r_reach)

        # ---- termination ----
        fell = (pos[:, 2] < self.stand_height * 0.6) | (upright < 0.4) | ~torch.isfinite(pos).all(-1)
        timeout = self.step_count >= self.max_steps
        reward = reward - 2.0 * fell.float()
        done = fell | timeout

        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0

        # stats accumulators (GPU only; read once per iteration)
        df = done.float()
        self._acc["ret_sum"] += (self.episode_return * df).sum()
        self._acc["len_sum"] += (self.episode_len * df).sum()
        self._acc["fell_sum"] += (fell.float() * df).sum()
        self._acc["done_n"] += df.sum()
        self._acc["v_sum"] += v_toward.mean()
        self._acc["reach_sum"] += reached.float().mean()
        self._acc["upright_sum"] += upright.mean()
        self._acc["steps"] += 1.0

        # new target for envs that reached theirs (no reset), then masked resets for done envs
        self.targets = torch.where(reached[:, None], self._random_targets(pos[:, :2]), self.targets)
        self._apply_reset(done)
        mjw.forward(self.mw, self.dw)
        self._obs = self.compute_obs()
        return self._obs, reward, done, timeout

    def get_stats(self) -> Dict[str, float]:
        a = {k: v.item() for k, v in self._acc.items()}
        n = max(1.0, a["done_n"]); s = max(1.0, a["steps"])
        out = {"ep_return": a["ret_sum"] / n, "ep_len_s": a["len_sum"] / n * self.dt, "fall_rate": a["fell_sum"] / n,
               "v_toward": a["v_sum"] / s, "reach_frac": a["reach_sum"] / s, "upright": a["upright_sum"] / s}
        for v in self._acc.values(): v.zero_()
        return out

    # ---- for evaluation with a fixed far target (100 m dash) --------------------------------------
    def set_targets(self, xy: torch.Tensor) -> None:
        self.targets = xy.clone()
