"""One athlete, one policy, stepped in plain MuJoCo on the CPU.

Shared by `view_policy.py` (watch it) and `eval_100m.py` (measure it). It exists as its own module
because the observation contract had drifted into three separate copies and two of them were wrong:
`eval_100m.py` still built the 75-float vector from before `foot_contact` and `base_height` were
added, still clamped actions at the old +-5, and still opened with the old 20 mm reset drop. A policy
handed the wrong numbers in the right shape fails silently -- there is no error to read.

So there is one implementation now. It builds exactly what `envs/run_to_target.py` builds, including
the lowest-corner foot contact test, and it reads the action clip, action scale, spawn clearance and
contact height out of `athlete_policy_config.json` rather than hard-coding any of them.

It drives the torch checkpoint rather than the ONNX export: the two are equivalent (`ExportPolicy`
just bakes the observation normaliser into the graph) and onnxruntime is not installed in this venv.
"""
from __future__ import annotations

import json
import math
import os
import sys

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ppo import ActorCritic  # noqa: E402


def quat_rotate_inverse(q, v):
    w, x, y, z = q
    xyz = np.array([x, y, z])
    t = 2.0 * np.cross(xyz, v)
    return v - w * t + np.cross(xyz, t)


def quat_yaw(q):
    w, x, y, z = q
    return math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


class Rollout:
    """One athlete, one policy, stepped at the trainer's control rate."""

    def __init__(self, xml: str, ckpt: str, target_dist: float, seed: int,
                 reset_jitter: float = 0.05, action_scale: float | None = None,
                 gait_period: float = 0.8):
        self.m = mujoco.MjModel.from_xml_path(xml)
        self.d = mujoco.MjData(self.m)
        with open(os.path.splitext(xml)[0] + "_policy_config.json", encoding="utf-8") as f:
            self.cfg = json.load(f)
        ck = torch.load(ckpt, map_location="cpu", weights_only=False)
        extra = ck.get("extra", {})
        self.n_obs = int(extra.get("obs_dim", ck["obs_rms"]["mean"].numel()))
        self.A = int(extra.get("act_dim", self.m.nu))
        self.net = ActorCritic(self.n_obs, self.A)
        self.net.load_state_dict(ck["model"])
        self.net.eval()
        # The exported ONNX bakes the observation normaliser into the graph; driving the raw actor
        # means applying it here instead, with the same clip the trainer used.
        self.obs_mean = ck["obs_rms"]["mean"].float()
        self.obs_std = torch.sqrt(ck["obs_rms"]["var"].float() + 1e-8)
        self.obs_clip = 10.0
        self.iters = int(extra.get("iter", 0))
        self.decimation = int(self.cfg.get("control_decimation", 4))
        # What the checkpoint was trained with wins over what the model file currently says: the
        # config json carries whatever `rig_to_mjcf.py` last wrote, and a policy driven at a
        # different action scale than it learned at falls over for no visible reason.
        self.gait_period = gait_period
        self.phase = 0.0
        self.action_scale = float(action_scale if action_scale is not None
                                  else extra.get("action_scale", self.cfg.get("action_scale", 0.5)))
        # Read from the config rather than hard-coded: these two drifted apart from the trainer once
        # before (see STAGED_TUNING.md item 7) and the whole point of putting them in the JSON was so
        # that could not happen again.
        self.action_clip = float(self.cfg.get("action_clip", 3.0))
        self.spawn_clearance = float(self.cfg.get("spawn_clearance_m", 0.002))
        self.control_dt = float(self.m.opt.timestep) * self.decimation

        self.default = self.m.key_qpos[0][7:].copy()
        self.stand_h = float(self.m.key_qpos[0][2])
        self.lo = self.m.actuator_ctrlrange[:, 0]
        self.hi = self.m.actuator_ctrlrange[:, 1]
        self.pelvis = mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
        self.foot_sites = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_SITE, f"foot_{s}_site")
                           for s in "lr"]
        self.foot_geoms = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_GEOM, f"foot_{s}_geom")
                           for s in "lr"]
        fg = self.foot_geoms[0]
        self.sole_half = float(self.m.geom_size[fg, 2])
        self.foot_half = self.m.geom_size[fg].copy()
        self.sole_contact_h = float(self.cfg.get("sole_contact_height", 0.03))

        self.target_dist = target_dist
        self.reset_jitter = reset_jitter
        self.rng = np.random.default_rng(seed)
        self.episode = 0
        self.reset()

    # ---- episode ---------------------------------------------------------------------------
    def reset(self) -> None:
        mujoco.mj_resetDataKeyframe(self.m, self.d, 0)
        self.d.qpos[2] += self.spawn_clearance
        # The same jitter the trainer resets with (envs/run_to_target._reset_states): +-0.05 rad on
        # every joint and a little base drift. Without it every episode from a given checkpoint is
        # bit-identical, so running five of them tells you exactly what running one does.
        self.d.qpos[7:] += self.rng.uniform(-self.reset_jitter, self.reset_jitter, size=self.A)
        self.d.qvel[:2] = self.rng.uniform(-0.2, 0.2, size=2)
        self.d.ctrl[:] = self.d.qpos[7:].copy()
        mujoco.mj_forward(self.m, self.d)
        self.last_action = np.zeros(self.A, np.float32)
        self.episode += 1
        self.t0 = self.d.time
        self.peak = 0.0
        self.phase = 0.0
        self.new_target()

    def new_target(self) -> None:
        ang = self.rng.uniform(0.0, 2.0 * math.pi)
        self.target = self.d.xpos[self.pelvis][:2] + self.target_dist * np.array(
            [math.cos(ang), math.sin(ang)])

    def observe(self) -> np.ndarray:
        d, m = self.d, self.m
        q = d.xquat[self.pelvis]
        pos = d.xpos[self.pelvis]
        lin_b = quat_rotate_inverse(q, d.qvel[:3])
        ang_b = d.qvel[3:6]
        grav_b = quat_rotate_inverse(q, np.array([0.0, 0.0, -1.0]))
        yaw = quat_yaw(q)
        to_t = self.target - pos[:2]
        dist = float(np.linalg.norm(to_t))
        c, s = math.cos(yaw), math.sin(yaw)
        dx = c * to_t[0] + s * to_t[1]
        dy = -s * to_t[0] + c * to_t[1]
        cmd = np.array([dx / max(dist, 1e-3), dy / max(dist, 1e-3), min(dist, 10.0) / 10.0])

        parts = [lin_b, ang_b, grav_b, cmd, d.qpos[7:] - self.default, d.qvel[6:], self.last_action]
        # n_obs is the contract: 78 is the foot-contact/base-height layout, 80 adds the gait clock.
        # A policy trained with the clock cannot be driven without it, and the phase has to advance
        # here at exactly the rate the trainer advances it (once per control step).

        if self.n_obs >= 78:
            # foot_contact(2) + base_height(1): added with the gait reward, and a policy trained with
            # them cannot be driven without them.
            # Lowest corner of the foot box, matching envs/run_to_target._foot_state. A flat-foot
            # estimate reads this policy as airborne while it runs on its toes.
            parts.append(self.foot_contact().astype(np.float64))
            parts.append(np.array([pos[2]]))
        if self.n_obs >= 80:
            tau = 2.0 * math.pi * self.phase
            parts.append(np.array([math.sin(tau), math.cos(tau)]))
        obs = np.concatenate(parts).astype(np.float32)
        return np.clip(np.nan_to_num(obs), -100.0, 100.0)

    def foot_contact(self) -> np.ndarray:
        """(2,) bool: is each foot's lowest box corner within `sole_contact_height` of the deck?

        The lowest *corner*, not the sole plane. This foot is 0.317 m long and the athlete runs on
        its toes, so a flat-foot estimate reads a planted foot as airborne -- measured at 1.7 %
        against MuJoCo's own 61 %. Matches `envs/run_to_target._foot_state` exactly; it is public so
        a gait trace and the observation cannot drift apart.
        """
        d = self.d
        cz = d.geom_xpos[self.foot_geoms][:, 2]
        rz = d.geom_xmat[self.foot_geoms].reshape(-1, 3, 3)[:, 2, :]
        sole_z = cz - (np.abs(rz) * self.foot_half).sum(-1)
        return sole_z < self.sole_contact_h

    def step(self) -> dict:
        obs = self.observe()
        if obs.shape[0] != self.n_obs:
            raise SystemExit(
                f"observation mismatch: this checkpoint wants {self.n_obs} floats, the rig builds "
                f"{obs.shape[0]}. The checkpoint predates the current observation contract.")
        with torch.no_grad():
            x = torch.from_numpy(obs)
            x = torch.clamp((x - self.obs_mean) / self.obs_std, -self.obs_clip, self.obs_clip)
            act = self.net.actor(x).numpy()          # deterministic: the mean, no exploration noise
        act = np.clip(act, -self.action_clip, self.action_clip).astype(np.float32)
        self.last_action = act
        self.d.ctrl[:] = np.clip(self.default + act * self.action_scale, self.lo, self.hi)
        for _ in range(self.decimation):
            mujoco.mj_step(self.m, self.d)
        self.phase = (self.phase + self.decimation * self.m.opt.timestep / self.gait_period) % 1.0

        pos = self.d.xpos[self.pelvis]
        upright = float(-quat_rotate_inverse(self.d.xquat[self.pelvis],
                                             np.array([0.0, 0.0, -1.0]))[2])
        speed = float(np.linalg.norm(self.d.qvel[:2]))
        self.peak = max(self.peak, speed)
        dist = float(np.linalg.norm(self.target - pos[:2]))
        if dist < 0.6:
            self.new_target()
        fell = pos[2] < self.stand_h * 0.6 or upright < 0.4
        return {"fell": fell, "speed": speed, "upright": upright,
                "alive": self.d.time - self.t0, "dist": dist}
