"""Score a lap policy on the lap, deterministically.

`eval_100m.py` runs a straight sprint at a target 108 m away, which puts `min(dist, 10) / 10` at 1.0
for the first 98 metres. A policy trained by `run_track` has never seen that: its carrot sits a fixed
`lookahead` (6 m) ahead, so the same observation slot is 0.6 for the whole of training. The sprint is
therefore an out-of-distribution test for a lap policy -- useful as a robustness probe, misleading as
a ranking, and it is how a checkpoint came to look brittle when the fault may have been the test.

This runs the actual task: `RunTrackEnv`, the same carrot, the same 5.3 m deck that resets an athlete
that wanders off it. Actions are the actor mean rather than samples, matching what Unity executes
through `PolicyRunner`, so two checkpoints can be ranked on how they will really behave.

    .venv/Scripts/python.exe eval_lap.py --ckpt checkpoints/run_track/model_03800.pt

Reports metres per second along the centre line, the lap time that implies, and how often an athlete
went down or off the deck -- which is the number that decides whether "faster" is actually better.
"""
from __future__ import annotations

import argparse
import os
import sys

import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from envs.run_track import RunTrackEnv  # noqa: E402
from ppo import PPO, PPOConfig, ExportPolicy  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--num-envs", type=int, default=256)
    ap.add_argument("--seconds", type=float, default=30.0)
    ap.add_argument("--target-speed", type=float, default=6.0,
                    help="the command the policy is given, not a cap on what it does")
    ap.add_argument("--device", default="cuda")
    args = ap.parse_args()

    # Randomisation off: this is a ranking on the nominal rig, and the whole point is that two
    # checkpoints see identical conditions.
    env = RunTrackEnv(args.xml, args.num_envs, device=args.device, seed=0,
                      target_speed=args.target_speed, domain_rand=False)

    ppo = PPO(env.obs_dim, env.A, args.num_envs, args.device, PPOConfig())
    extra = ppo.load(args.ckpt)
    policy = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip).to(args.device).eval()

    obs = env.reset()
    start = env._lap_progress.clone()
    steps = max(1, int(args.seconds / env.dt))
    ended_early = torch.zeros(args.num_envs, device=args.device)

    with torch.no_grad():
        for _ in range(steps):
            obs, _, done, timeout = env.step(policy(obs))
            # A done that is not the clock is a fall or a wander off the deck. Either way the athlete
            # stopped racing, which is the thing a lap time has to be read against.
            ended_early += (done & ~timeout).float()

    travelled = (env._lap_progress - start)
    speed = travelled.mean().item() / (steps * env.dt)
    incidents = ended_early.mean().item()
    clean = (ended_early == 0).float().mean().item()

    print(f"checkpoint      {os.path.basename(args.ckpt)} (iter {extra.get('iter', '?')})")
    print(f"speed           {speed:.2f} m/s along the centre line")
    print(f"lap time        {env.lap / max(speed, 1e-3):.2f} s over {env.lap:.1f} m")
    print(f"incidents       {incidents:.2f} per athlete in {args.seconds:.0f} s (falls or off the deck)")
    print(f"clean athletes  {clean * 100:.0f}%")


if __name__ == "__main__":
    main()
