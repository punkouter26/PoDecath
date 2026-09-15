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
    ap.add_argument("--seconds", type=float, default=30.0,
                    help="length of one evaluation episode; every athlete is reset between episodes")
    ap.add_argument("--episodes", type=int, default=1,
                    help="consecutive evaluation episodes; the KPI gate needs every one of them to pass")
    ap.add_argument("--target-speed", type=float, default=6.0,
                    help="the command the policy is given, not a cap on what it does")
    ap.add_argument("--min-clean", type=float, default=0.90, help="KPI: fraction of athletes with no fall / off-deck")
    ap.add_argument("--speed-tol", type=float, default=0.10, help="KPI: |speed - target| / target")
    ap.add_argument("--max-torque", type=float, default=0.0, help="KPI: mean |torque| per joint, N m (0 = not gated)")
    ap.add_argument("--max-jerk", type=float, default=0.0, help="KPI: RMS joint jerk, rad/s^3 (0 = not gated)")
    ap.add_argument("--json", default="", help="append one JSON line per run to this file")
    ap.add_argument("--label", default="")
    ap.add_argument("--gait-period", type=float, default=0.8)
    ap.add_argument("--gait-duty", type=float, default=0.6)
    ap.add_argument("--init-speed", type=float, default=0.0,
                    help="diagnostic only: start episodes already moving at U[0, this] m/s. The official "
                         "gate always starts from a standstill; this separates cruise speed from window "
                         "speed, whose difference is the acceleration phase.")
    ap.add_argument("--device", default="cuda")
    args = ap.parse_args()

    # Randomisation off: this is a ranking on the nominal rig, and the whole point is that two
    # checkpoints see identical conditions.
    # The environment has to match the checkpoint's contract: 80 observations means the gait clock is
    # on, and the action scale is whatever the run was trained at (0.167 for the v80 runs, not the
    # 0.5 default -- driven at 0.5 a v80 policy asks every joint for three times its trained travel).
    ck = torch.load(args.ckpt, map_location="cpu")
    ex = ck.get("extra", {})
    obs_dim = int(ex.get("obs_dim") or ck["model"]["actor.0.weight"].shape[1])
    action_scale = float(ex.get("action_scale", 0.5))
    gait_w = 1.0 if obs_dim >= 80 else 0.0
    env = RunTrackEnv(args.xml, args.num_envs, device=args.device, seed=0,
                      target_speed=args.target_speed, domain_rand=False,
                      action_scale=action_scale, gait_w=gait_w,
                      gait_period=args.gait_period, gait_duty=args.gait_duty,
                      init_speed=args.init_speed)
    print(f"contract        obs {obs_dim}, action_scale {action_scale:g}, gait clock {'on' if gait_w > 0 else 'off'}"
          f"{f', warm start U[0,{args.init_speed:g}] m/s' if args.init_speed > 0 else ' (standing start)'}")
    print(f"contract        obs {obs_dim}, action_scale {action_scale:g}, gait clock {'on' if gait_w > 0 else 'off'}")

    ppo = PPO(env.obs_dim, env.A, args.num_envs, args.device, PPOConfig())
    extra = ppo.load(args.ckpt)
    policy = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip).to(args.device).eval()

    steps = max(1, int(args.seconds / env.dt))
    episodes = []
    for ep in range(max(1, args.episodes)):
        obs = env.reset()
        env.get_stats()                       # zero the KPI accumulators for this episode
        # Distance is accumulated here, per step, from the arc length the env projects the pelvis
        # onto. The env's own _lap_progress cannot be used: reset() randomises every athlete's episode
        # clock, so their 20 s time-outs land at random moments inside this window and each one zeroes
        # the counter -- which is how a 3.1 m/s policy came to be scored at 1.7 m/s. A step on which an
        # athlete was reset (fall, off the deck, or the clock) contributes nothing.
        s_prev = env._s.clone()
        travelled = torch.zeros(args.num_envs, device=args.device)
        ended_early = torch.zeros(args.num_envs, device=args.device)
        with torch.no_grad():
            for _ in range(steps):
                obs, _, done, timeout = env.step(policy(obs))
                ds = torch.remainder(env._s - s_prev + env.lap * 0.5, env.lap) - env.lap * 0.5
                travelled += torch.where(done, torch.zeros_like(ds), ds)
                s_prev = env._s.clone()
                # A done that is not the clock is a fall or a wander off the deck. Either way the
                # athlete stopped racing, which is the thing a lap time has to be read against.
                ended_early += (done & ~timeout).float()

        clean_mask = ended_early == 0
        clean = clean_mask.float().mean().item()
        # Speed over the athletes that stayed on their feet: a reset mid-episode restarts the lap
        # counter, so a fallen athlete's distance says nothing about pace.
        speed = (travelled[clean_mask].mean().item() / (steps * env.dt)) if clean_mask.any() else 0.0
        s = env.get_stats()
        rec = {
            "episode": ep + 1, "clean": clean, "speed": speed,
            "speed_err": abs(speed - args.target_speed) / max(1e-6, abs(args.target_speed)),
            "incidents": ended_early.mean().item(),
            "v_err": s.get("v_err", 0.0), "torque": s.get("torque", 0.0), "power": s.get("power", 0.0),
            "jerk": s.get("jerk", 0.0), "pitch_dev": s.get("pitch_dev", 0.0), "roll_dev": s.get("roll_dev", 0.0),
            "act_sat": s.get("act_sat", 0.0), "duty": s.get("duty_factor", 0.0), "slip": s.get("foot_slip", 0.0),
        }
        rec["pass"] = (clean >= args.min_clean and rec["speed_err"] <= args.speed_tol
                       and (args.max_torque <= 0 or rec["torque"] <= args.max_torque)
                       and (args.max_jerk <= 0 or rec["jerk"] <= args.max_jerk))
        episodes.append(rec)
        print(f"ep {ep + 1:2d} | clean {clean * 100:3.0f}% | speed {speed:4.2f} m/s ({rec['speed_err'] * 100:4.1f}% off) "
              f"| tau {rec['torque']:5.1f} Nm | jerk {rec['jerk']:6.0f} | pitch/roll {rec['pitch_dev']:4.1f}/{rec['roll_dev']:4.1f} deg "
              f"| {'PASS' if rec['pass'] else 'fail'}")

    n_pass = sum(1 for e in episodes if e["pass"])
    mean = lambda k: sum(e[k] for e in episodes) / len(episodes)
    print(f"checkpoint      {os.path.basename(args.ckpt)} (iter {extra.get('iter', '?')})")
    print(f"speed           {mean('speed'):.2f} m/s along the centre line (clean athletes)")
    print(f"lap time        {env.lap / max(mean('speed'), 1e-3):.2f} s over {env.lap:.1f} m")
    print(f"incidents       {mean('incidents'):.2f} per athlete in {args.seconds:.0f} s (falls or off the deck)")
    print(f"clean athletes  {mean('clean') * 100:.0f}%")
    print(f"KPI gate        {n_pass}/{len(episodes)} episodes pass "
          f"(clean >= {args.min_clean:.2f}, speed within {args.speed_tol * 100:.0f}% of {args.target_speed:g} m/s"
          f"{', torque <= %g' % args.max_torque if args.max_torque > 0 else ''}"
          f"{', jerk <= %g' % args.max_jerk if args.max_jerk > 0 else ''})")

    if args.json:
        import json, time
        row = {"label": args.label or os.path.basename(os.path.dirname(args.ckpt)), "ckpt": args.ckpt,
               "iter": extra.get("iter"), "target_speed": args.target_speed, "seconds": args.seconds,
               "init_speed": args.init_speed, "num_envs": args.num_envs, "when": time.strftime("%Y-%m-%d %H:%M:%S"),
               "episodes_pass": n_pass, "episodes": len(episodes),
               "mean": {k: mean(k) for k in ("clean", "speed", "speed_err", "v_err", "torque", "power",
                                             "jerk", "pitch_dev", "roll_dev", "act_sat", "duty", "slip")},
               "per_episode": episodes}
        with open(args.json, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(row) + "\n")


if __name__ == "__main__":
    main()
