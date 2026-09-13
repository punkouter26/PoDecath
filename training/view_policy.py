"""Watch a trained policy drive the athlete in MuJoCo's own viewer (house rule 11).

    .venv/Scripts/python.exe view_policy.py
    .venv/Scripts/python.exe view_policy.py --ckpt checkpoints/run_to_target/model_00200.pt --speed 0.25

Reward curves cannot tell you whether a gait looks like running. This can. The observation is built
exactly as `envs/run_to_target.py` builds it -- the same 78 floats in the same order, including
`foot_contact` and `base_height` -- so what the policy sees here is what it was trained on.

Controls are the viewer's own: drag to orbit, scroll to zoom, space to pause, `[`/`]` to step frames.
The camera tracks the pelvis. On a fall the episode resets after a short pause so the run continues.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time

import mujoco
import mujoco.viewer
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

    def __init__(self, xml: str, ckpt: str, target_dist: float, seed: int):
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
        self.action_scale = float(self.cfg.get("action_scale", 0.5))
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
        fg = mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_GEOM, "foot_l_geom")
        self.sole_half = float(self.m.geom_size[fg, 2])
        self.sole_contact_h = float(self.cfg.get("sole_contact_height", 0.03))

        self.target_dist = target_dist
        self.rng = np.random.default_rng(seed)
        self.episode = 0
        self.reset()

    # ---- episode ---------------------------------------------------------------------------
    def reset(self) -> None:
        mujoco.mj_resetDataKeyframe(self.m, self.d, 0)
        self.d.qpos[2] += self.spawn_clearance
        self.d.ctrl[:] = self.default
        mujoco.mj_forward(self.m, self.d)
        self.last_action = np.zeros(self.A, np.float32)
        self.episode += 1
        self.t0 = self.d.time
        self.peak = 0.0
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
        if self.n_obs >= 78:
            # foot_contact(2) + base_height(1): added with the gait reward, and a policy trained with
            # them cannot be driven without them.
            sole_z = d.site_xpos[self.foot_sites][:, 2] - self.sole_half
            parts.append((sole_z < self.sole_contact_h).astype(np.float64))
            parts.append(np.array([pos[2]]))
        obs = np.concatenate(parts).astype(np.float32)
        return np.clip(np.nan_to_num(obs), -100.0, 100.0)

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


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", default=os.path.join(HERE, "checkpoints", "run_to_target", "latest.pt"),
                    help="torch checkpoint to drive. The .onnx export is equivalent but needs "
                         "onnxruntime, which this venv does not have.")
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--speed", type=float, default=1.0,
                    help="playback rate. 0.25 is the useful one for looking at footfalls.")
    ap.add_argument("--target-dist", type=float, default=10.0, help="metres to the running target")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--hold", type=float, default=1.0, help="seconds to linger on a fall before reset")
    args = ap.parse_args()

    r = Rollout(args.xml, args.ckpt, args.target_dist, args.seed)
    print(f"policy  {args.ckpt}  (trained {r.iters} iterations)")
    print(f"obs     {r.n_obs} floats, {r.A} actuators, control {1.0 / r.control_dt:.0f} Hz, "
          f"action clip +-{r.action_clip:g}")
    print("drag to orbit, scroll to zoom, space pauses. Ctrl-C or close the window to stop.\n")

    best = 0.0
    with mujoco.viewer.launch_passive(r.m, r.d) as v:
        v.cam.distance = 6.0
        v.cam.elevation = -15.0
        while v.is_running():
            tick = time.perf_counter()
            s = r.step()
            # Track the pelvis so the athlete does not run out of frame.
            v.cam.lookat[:] = r.d.xpos[r.pelvis]
            v.sync()
            if s["fell"]:
                best = max(best, s["alive"])
                print(f"episode {r.episode:3d}  stayed up {s['alive']:5.2f}s  "
                      f"peak {r.peak:4.2f} m/s  (best so far {best:5.2f}s)", flush=True)
                time.sleep(args.hold)
                r.reset()
                continue
            lag = r.control_dt / max(args.speed, 1e-3) - (time.perf_counter() - tick)
            if lag > 0:
                time.sleep(lag)


if __name__ == "__main__":
    main()
