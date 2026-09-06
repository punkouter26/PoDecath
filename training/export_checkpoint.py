"""Export any saved PPO checkpoint to ONNX.

`train_run.py` exports the policy it happens to be holding at save time, which is the newest one and
not necessarily the best one. That distinction matters for anything trained against a limit: the
adaptive speed curriculum deliberately hunts around the fastest pace the athlete can hold, so
consecutive checkpoints alternate between clean 20-second episodes and fall-heavy ones, and whichever
of those the last iteration happens to land on is what ships. Picking by measured behaviour instead
needs a way to export an arbitrary checkpoint, which is this.

    .venv/Scripts/python.exe export_checkpoint.py --ckpt checkpoints/run_track/model_03400.pt \
                                                  --out  /tmp/track_3400.onnx
    .venv/Scripts/python.exe eval_100m.py --onnx /tmp/track_3400.onnx --runs 5
"""
from __future__ import annotations

import argparse
import os
import sys

import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from ppo import PPO, PPOConfig, export_onnx  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ckpt", required=True, help="the .pt to export")
    ap.add_argument("--out", required=True, help="destination .onnx")
    ap.add_argument("--obs-dim", type=int, default=0,
                    help="0 (default) reads it from the checkpoint's own extra block")
    ap.add_argument("--act-dim", type=int, default=0, help="0 (default) reads it from the checkpoint")
    args = ap.parse_args()

    ck = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    extra = ck.get("extra", {}) or {}
    obs_dim = args.obs_dim or int(extra.get("obs_dim", 75))
    act_dim = args.act_dim or int(extra.get("act_dim", 21))

    # num_envs=1 on purpose. PPO allocates its rollout buffers in the constructor, sized
    # steps_per_env x num_envs x obs_dim, and none of that is touched by an export.
    ppo = PPO(obs_dim, act_dim, 1, "cpu", PPOConfig())
    ppo.load(args.ckpt)
    export_onnx(ppo, args.out, obs_dim)

    print(f"{os.path.basename(args.ckpt)} (iter {extra.get('iter', '?')}, "
          f"obs {obs_dim}, act {act_dim}) -> {args.out}")


if __name__ == "__main__":
    main()
