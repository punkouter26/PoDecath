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
import json
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
    ap.add_argument("--manifest", default="",
                    help="also write the policy contract here (the observation groups this policy "
                         "emits, its action scale and its stride period). Unity reads this file to "
                         "decide what to build; without it the rig's own config is used, which "
                         "describes the body rather than the policy.")
    ap.add_argument("--gait-period", type=float, default=0.8,
                    help="stride period the checkpoint was trained with, written into --manifest")
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
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

    if args.manifest:
        with open(os.path.splitext(args.xml)[0] + "_policy_config.json", encoding="utf-8") as fh:
            cfg = json.load(fh)
        base = 12 + 3 * act_dim + 3          # the layout through foot_contact + base_height
        obs = [o for o in cfg.get("observation", []) if o != "gait_phase"]
        if obs_dim == base + 2:
            obs.append("gait_phase")
            cfg["gait_period"] = args.gait_period
        elif obs_dim != base:
            raise SystemExit(f"cannot describe a {obs_dim}-float policy: this rig builds {base} "
                             f"without the gait clock and {base + 2} with it")
        cfg["observation"] = obs
        cfg["observation_size"] = obs_dim
        cfg["action_scale"] = float(extra.get("action_scale", cfg.get("action_scale", 0.5)))
        cfg["trained_by"] = {"checkpoint": os.path.basename(args.ckpt),
                             "run": extra.get("run_name", "?"), "iter": extra.get("iter", "?")}
        with open(args.manifest, "w", encoding="utf-8") as fh:
            json.dump(cfg, fh, indent=2)
        print(f"  manifest -> {args.manifest} "
              f"(obs {obs_dim}: {', '.join(obs)}; action_scale {cfg['action_scale']})")


if __name__ == "__main__":
    main()
