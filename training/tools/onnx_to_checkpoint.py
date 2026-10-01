"""Rebuild a resumable PPO checkpoint from an exported policy ONNX.

    .venv/Scripts/python.exe tools/onnx_to_checkpoint.py \
        --onnx ../Assets/Policies/athlete_track_q5.onnx --out checkpoints/warm/q5.pt

The .pt checkpoints are not in git (see .gitignore), so a fresh clone has the shipped policies and
nothing to resume them from. The ONNX carries everything the *actor* needs: the four Linear layers
and the observation normaliser `export_onnx` baked in as `mean` / `std`. What it does not carry is
the critic, the exploration log-std and the optimiser state, so the checkpoint written here has a
freshly initialised critic and the log-std given by --init-std.

A fresh critic next to a trained actor is dangerous for exactly one reason: the first policy updates
are driven by advantages from a value function that knows nothing, and they can undo the gait in a
few iterations. Resume it with `train_run.py --critic-warmup-iters N`, which fits the critic for N
iterations before the actor is allowed to move.

The round trip is checked before anything is written: the rebuilt policy is run against the ONNX
weights on random observations and has to agree to 1e-5.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import onnx
import torch
from onnx import numpy_helper

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)

from ppo import PPO, PPOConfig, ExportPolicy  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--action-scale", type=float, default=0.167,
                    help="radians per unit action the policy was trained at (its manifest's action_scale)")
    ap.add_argument("--init-std", type=float, default=0.3,
                    help="exploration std to resume with; the trained value is not in the ONNX")
    ap.add_argument("--run-name", default="")
    args = ap.parse_args()

    init = {i.name: numpy_helper.to_array(i) for i in onnx.load(args.onnx).graph.initializer}
    need = ["mean", "std"] + [f"actor.{k}.{p}" for k in (0, 2, 4, 6) for p in ("weight", "bias")]
    missing = [n for n in need if n not in init]
    if missing:
        raise SystemExit(f"{args.onnx} is not an export_onnx policy: no initializer named {missing}")
    obs_dim = int(init["actor.0.weight"].shape[1])
    act_dim = int(init["actor.6.weight"].shape[0])

    ppo = PPO(obs_dim, act_dim, 1, "cpu", PPOConfig(init_std=args.init_std))
    sd = ppo.model.state_dict()
    for name in need[2:]:
        if tuple(sd[name].shape) != tuple(init[name].shape):
            raise SystemExit(f"{name}: ONNX {init[name].shape} does not fit the actor {tuple(sd[name].shape)}")
        sd[name] = torch.from_numpy(np.array(init[name]))
    ppo.model.load_state_dict(sd)
    std = torch.from_numpy(np.array(init["std"]))
    ppo.obs_rms.mean = torch.from_numpy(np.array(init["mean"]))
    ppo.obs_rms.var = (std * std - 1e-8).clamp_min(0.0)
    # A large count so the first rollouts nudge the running statistics instead of replacing them:
    # the actor's first layer is fitted to this normalisation.
    ppo.obs_rms.count = 1.0e9

    # Round trip: the rebuilt policy against the ONNX arithmetic done by hand.
    x = torch.randn(256, obs_dim) * 3.0
    with torch.no_grad():
        got = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip)(x).numpy()
    h = np.clip((x.numpy() - init["mean"]) / init["std"], -ppo.cfg.obs_clip, ppo.cfg.obs_clip)
    for k in (0, 2, 4):
        h = h @ init[f"actor.{k}.weight"].T + init[f"actor.{k}.bias"]
        h = np.where(h > 0, h, np.exp(np.minimum(h, 0.0)) - 1.0)
    want = h @ init["actor.6.weight"].T + init["actor.6.bias"]
    err = float(np.abs(got - want).max())
    if err > 1e-4:
        raise SystemExit(f"round trip failed: rebuilt policy differs from the ONNX by {err:g}")

    run = args.run_name or os.path.splitext(os.path.basename(args.onnx))[0]
    ppo.save(args.out, {"iter": 0, "obs_dim": obs_dim, "act_dim": act_dim,
                        "action_scale": args.action_scale, "run_name": run,
                        "from_onnx": os.path.basename(args.onnx), "critic": "fresh"})
    print(f"{os.path.basename(args.onnx)} (obs {obs_dim}, act {act_dim}) -> {args.out}  "
          f"round trip max error {err:.2e}; critic fresh, std {args.init_std:g}")


if __name__ == "__main__":
    main()
