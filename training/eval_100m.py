"""Evaluate an exported ONNX policy on a 100 m dash in plain MuJoCo (CPU, same physics settings).

    .venv/Scripts/python.exe eval_100m.py --onnx checkpoints/run_to_target/latest.onnx --runs 5

Reports finish time, whether the athlete fell, and peak speed. Mirrors the Unity DashEvent so a
policy that passes here should pass in the Rooftop scene (same observation contract).
"""
from __future__ import annotations

import argparse
import json
import math
import os

import mujoco
import numpy as np
import onnxruntime as ort

HERE = os.path.dirname(os.path.abspath(__file__))


def quat_rotate_inverse(q, v):
    w, x, y, z = q
    xyz = np.array([x, y, z])
    t = 2.0 * np.cross(xyz, v)
    return v - w * t + np.cross(xyz, t)


def quat_yaw(q):
    w, x, y, z = q
    return math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def run(m, d, sess, cfg, target_xy, max_s=40.0, decimation=4, action_scale=0.5, seed=0):
    rng = np.random.default_rng(seed)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    d.qpos[2] += 0.02
    default = m.key_qpos[0][7:].copy()
    d.ctrl[:] = default
    mujoco.mj_forward(m, d)
    A = m.nu
    last_action = np.zeros(A, np.float32)
    lo, hi = m.actuator_ctrlrange[:, 0], m.actuator_ctrlrange[:, 1]
    stand_h = float(m.key_qpos[0][2])
    start_x = d.qpos[0]
    t_finish = None
    fell = False
    peak = 0.0
    steps = int(max_s / (m.opt.timestep * decimation))
    for k in range(steps):
        q = d.xquat[1]; pos = d.xpos[1]
        lin_b = quat_rotate_inverse(q, d.qvel[:3])
        ang_b = d.qvel[3:6]
        grav_b = quat_rotate_inverse(q, np.array([0, 0, -1.0]))
        yaw = quat_yaw(q)
        to_t = target_xy - pos[:2]
        dist = np.linalg.norm(to_t)
        c, s = math.cos(yaw), math.sin(yaw)
        dx = c * to_t[0] + s * to_t[1]; dy = -s * to_t[0] + c * to_t[1]
        cmd = np.array([dx / max(dist, 1e-3), dy / max(dist, 1e-3), min(dist, 10.0) / 10.0])
        obs = np.concatenate([lin_b, ang_b, grav_b, cmd, d.qpos[7:] - default, d.qvel[6:], last_action]).astype(np.float32)
        obs = np.clip(np.nan_to_num(obs), -100, 100)
        act = sess.run(None, {"obs": obs[None]})[0][0]
        act = np.clip(act, -5, 5).astype(np.float32)
        last_action = act
        d.ctrl[:] = np.clip(default + act * action_scale, lo, hi)
        for _ in range(decimation):
            mujoco.mj_step(m, d)
        speed = float(np.linalg.norm(d.qvel[:2]))
        peak = max(peak, speed)
        upright = -quat_rotate_inverse(d.xquat[1], np.array([0, 0, -1.0]))[2]
        if d.xpos[1][2] < stand_h * 0.6 or upright < 0.4:
            fell = True
            return {"finished": False, "time": d.time, "distance": float(d.qpos[0] - start_x), "fell": True, "peak_speed": peak}
        if d.qpos[0] - start_x >= 100.0:
            t_finish = d.time
            break
    return {"finished": t_finish is not None, "time": t_finish if t_finish else d.time,
            "distance": float(d.qpos[0] - start_x), "fell": fell, "peak_speed": peak}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", default=os.path.join(HERE, "checkpoints", "run_to_target", "latest.onnx"))
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--runs", type=int, default=5)
    args = ap.parse_args()
    m = mujoco.MjModel.from_xml_path(args.xml)
    d = mujoco.MjData(m)
    with open(os.path.splitext(args.xml)[0] + "_policy_config.json") as f:
        cfg = json.load(f)
    sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
    target = np.array([108.0, 0.0])   # finish line at 100 m, target a little beyond it
    results = []
    for i in range(args.runs):
        r = run(m, d, sess, cfg, target, seed=i, action_scale=cfg.get("action_scale", 0.5), decimation=cfg.get("control_decimation", 4))
        results.append(r)
        print(f"run {i}: finished={r['finished']} time={r['time']:.2f}s distance={r['distance']:.1f}m fell={r['fell']} peak={r['peak_speed']:.2f} m/s")
    ok = sum(1 for r in results if r["finished"] and not r["fell"])
    print(f"\n{ok}/{len(results)} clean 100 m finishes")


if __name__ == "__main__":
    main()
