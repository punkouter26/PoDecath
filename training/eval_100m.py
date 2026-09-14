"""Evaluate a policy on a 100 m dash in plain MuJoCo, deterministically.

    .venv/Scripts/python.exe eval_100m.py
    .venv/Scripts/python.exe eval_100m.py --ckpt checkpoints/run_to_target/model_01000.pt --runs 10

Reports finish time, distance, whether the athlete fell, and peak speed. Actions are the actor mean
rather than samples, matching what Unity executes through `PolicyRunner`, so two checkpoints can be
ranked on how they will really behave.

This used to carry its own copy of the observation layout and the copy had gone stale: it built the
75-float vector from before `foot_contact` and `base_height` existed, clamped actions at the old
+-5, and opened with the old 20 mm reset drop. It also needed onnxruntime, which is not installed in
this venv, so it could not run at all. It now shares `athlete_rollout.Rollout` with `view_policy.py`,
which is the one place the contract lives.

A caveat about what this measures. The dash puts the target 108 m away, which pins the distance
observation at its 1.0 ceiling for the first 98 m. A policy trained by `run_track` has never seen
that -- its carrot sits 6 m ahead, so the same slot reads 0.6 throughout training -- so this is an
out-of-distribution test for a lap policy. Use `eval_lap.py` to rank those.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from athlete_rollout import Rollout  # noqa: E402


def dash(r: Rollout, distance: float, max_s: float, seed: int) -> dict:
    r.rng = np.random.default_rng(seed)
    r.reset()
    # Straight down +x, far enough past the line that arriving never retargets mid-race.
    r.target = np.array([r.d.xpos[r.pelvis][0] + distance + 8.0, 0.0])
    start_x = float(r.d.xpos[r.pelvis][0])
    steps = int(max_s / r.control_dt)
    for _ in range(steps):
        s = r.step()
        # `Rollout` re-targets when it arrives; the dash wants one fixed heading, so put it back.
        r.target = np.array([start_x + distance + 8.0, 0.0])
        travelled = float(r.d.xpos[r.pelvis][0]) - start_x
        if s["fell"]:
            return {"finished": False, "fell": True, "time": s["alive"],
                    "distance": travelled, "peak": r.peak}
        if travelled >= distance:
            return {"finished": True, "fell": False, "time": s["alive"],
                    "distance": travelled, "peak": r.peak}
    return {"finished": False, "fell": False, "time": max_s,
            "distance": float(r.d.xpos[r.pelvis][0]) - start_x, "peak": r.peak}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ckpt", default=os.path.join(HERE, "checkpoints", "run_to_target", "latest.pt"))
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--action-scale", type=float, default=None,
                    help="override the action scale. Checkpoints saved from 2026-09-14 carry "
                         "their own; older ones take it from the model config, which may since "
                         "have been regenerated with a different value.")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--distance", type=float, default=100.0)
    ap.add_argument("--max-seconds", type=float, default=60.0)
    args = ap.parse_args()

    r = Rollout(args.xml, args.ckpt, target_dist=10.0, seed=0,
                action_scale=args.action_scale)
    print(f"checkpoint {os.path.basename(args.ckpt)} (iter {r.iters}), {r.n_obs} obs, {r.A} actions\n")

    rows = [dash(r, args.distance, args.max_seconds, seed=i) for i in range(args.runs)]
    for i, x in enumerate(rows):
        end = "FINISHED" if x["finished"] else ("fell" if x["fell"] else "timed out")
        print(f"run {i}: {end:9s} {x['time']:6.2f}s  {x['distance']:6.1f} m  peak {x['peak']:.2f} m/s")

    ok = [x for x in rows if x["finished"]]
    print(f"\n{len(ok)}/{len(rows)} clean {args.distance:.0f} m finishes")
    if ok:
        print(f"best {min(x['time'] for x in ok):.2f} s, mean {np.mean([x['time'] for x in ok]):.2f} s")
    # The honest summary when nothing finishes: how far it actually got, and how long it stayed up.
    print(f"mean distance {np.mean([x['distance'] for x in rows]):.1f} m, "
          f"mean upright {np.mean([x['time'] for x in rows]):.2f} s, "
          f"mean peak {np.mean([x['peak'] for x in rows]):.2f} m/s")


if __name__ == "__main__":
    main()
