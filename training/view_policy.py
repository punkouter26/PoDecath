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
import os
import sys
import time

import mujoco
import mujoco.viewer

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from athlete_rollout import Rollout  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", default=os.path.join(HERE, "checkpoints", "run_to_target", "latest.pt"),
                    help="torch checkpoint to drive. The .onnx export is equivalent but needs "
                         "onnxruntime, which this venv does not have.")
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--action-scale", type=float, default=None,
                    help="override the action scale. Checkpoints saved from 2026-09-14 carry "
                         "their own; older ones take it from the model config, which may since "
                         "have been regenerated with a different value.")
    ap.add_argument("--speed", type=float, default=1.0,
                    help="playback rate. 0.25 is the useful one for looking at footfalls.")
    ap.add_argument("--target-dist", type=float, default=10.0, help="metres to the running target")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--hold", type=float, default=1.0, help="seconds to linger on a fall before reset")
    args = ap.parse_args()

    r = Rollout(args.xml, args.ckpt, args.target_dist, args.seed,
                action_scale=args.action_scale)
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
