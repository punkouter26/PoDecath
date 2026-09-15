"""Train the run-to-target locomotion policy with PPO on MuJoCo Warp and export ONNX for Unity.

Usage (from training/):
    .venv/Scripts/python.exe train_run.py --num-envs 4096 --iters 1500
    .venv/Scripts/python.exe train_run.py --resume checkpoints/run_to_target/latest.pt

House rules applied here:
  * TensorBoard is launched automatically (http://localhost:6006) when training starts.
  * Stale TensorBoard runs for this task are removed first unless --keep-old-runs is passed.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import shutil
import subprocess
import sys
import time

import torch
from torch.utils.tensorboard import SummaryWriter

sys.path.insert(0, os.path.dirname(__file__))
from envs.get_up import GetUpEnv  # noqa: E402
from envs.run_to_target import RunToTargetEnv  # noqa: E402
from envs.run_track import RunTrackEnv  # noqa: E402
from envs.crowd import CrowdEnv  # noqa: E402
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
TASKS = {  # name -> (env class, exported ONNX file name in Assets/Policies)
    "target": ("run_to_target", RunToTargetEnv, "athlete_run.onnx"),
    "track": ("run_track", RunTrackEnv, "athlete_track.onnx"),
    "getup": ("get_up", GetUpEnv, "athlete_getup.onnx"),
    "crowd": ("crowd", CrowdEnv, "athlete_crowd.onnx"),
}
TASK = "run_to_target"


def launch_tensorboard(logdir: str, port: int) -> None:
    py = sys.executable
    try:
        subprocess.Popen([py, "-m", "tensorboard.main", "--logdir", logdir, "--port", str(port), "--bind_all"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
        print(f"[tensorboard] http://localhost:{port}  (logdir {logdir})")
    except Exception as e:  # pragma: no cover
        print(f"[tensorboard] failed to launch: {e}")


def clean_old_runs(tb_root: str, task: str) -> None:
    if not os.path.isdir(tb_root):
        return
    for name in os.listdir(tb_root):
        p = os.path.join(tb_root, name)
        if os.path.isdir(p) and name.startswith(task):
            shutil.rmtree(p, ignore_errors=True)
            print(f"[tensorboard] removed obsolete run {name}")


def write_policy_manifest(env, args, run_name: str, path: str) -> None:
    """Write the contract this *policy* was trained against, next to the exported ONNX.

    `models/athlete_policy_config.json` describes the rig, and Unity reads it to decide which
    observations to build. But two things in that decision belong to the training run, not the rig:
    `action_scale`, and whether the gait clock is in the observation at all. A run trained with
    --gait-w 1.0 produces an 80-float policy against the rig file's 78, and a run trained at
    --action-scale 0.167 driven at the rig file's 0.5 simply falls over -- with, in both cases,
    nothing on either side to say why.

    So the exported policy carries its own copy, with those fields corrected. Copy this file into
    Assets/Models/ alongside the .onnx and Unity is reading the same contract the policy learned.
    """
    cfg = dict(env.cfg)
    cfg["action_scale"] = env.action_scale
    obs = list(cfg.get("observation", []))
    if getattr(env, "gait_w", 0.0) > 0.0:
        if "gait_phase" not in obs:
            obs.append("gait_phase")
        cfg["gait_period"] = env.gait_period
        cfg["gait_duty"] = env.gait_duty
    else:
        obs = [o for o in obs if o != "gait_phase"]
        cfg.pop("gait_period", None)
        cfg.pop("gait_duty", None)
    cfg["observation"] = obs
    cfg["observation_size"] = env.obs_dim
    cfg["trained_by"] = {"run": run_name, "task": args.task,
                         "target_speed": env.target_speed}
    # What fraction of joint targets this policy pushed past the actuator range *in training*. Unity
    # measures the same thing live and grades its number against this one rather than against a
    # fixed threshold: the clamp is part of the trained behaviour, not a fault, until Unity clamps
    # materially more than the trainer did. Absent (0) on a manifest from before this field existed,
    # and Unity falls back to its old fixed threshold with a caveat in the verdict.
    cfg["train_target_clamp"] = float(getattr(env, "last_target_clamp", 0.0))
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(cfg, fh, indent=2)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--num-envs", type=int, default=8192,
                    help="Measured on this host (RTX 5070 Ti Laptop, 12 GB) with CUDA-graph capture on: "
                         "2048 -> 167k steps/s, 4096 -> 251k, 8192 -> 328k, 16384 -> 376k, 32768 -> 352k, "
                         "and VRAM never passes 8.4 GB. Throughput plateaus around 16k, but every extra "
                         "environment also enlarges the PPO batch without buying more gradient steps, so "
                         "8192 is the default: a third more throughput than the old 4096 with a batch the "
                         "update can still digest. --minibatches follows it automatically.")
    ap.add_argument("--minibatches", type=int, default=0,
                    help="0 = scale with --num-envs to hold the minibatch near 24k samples, which is what "
                         "4096 envs x 24 steps / 4 minibatches used to give.")
    ap.add_argument("--no-cuda-graph", action="store_true",
                    help="disable CUDA-graph capture of the physics block. Capture is worth roughly 8x on "
                         "this host and is the reason the heavier self-colliding model is affordable at "
                         "all; turn it off only to debug a physics problem it might be hiding.")
    ap.add_argument("--iters", type=int, default=1500)
    ap.add_argument("--steps", type=int, default=24)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--target-speed", type=float, default=3.5,
                    help="speed the running reward is peaked at, in m/s. Note this is a target, not a "
                         "floor: r_track is a Gaussian centred on it and r_prog is clamped to it, so "
                         "the policy is rewarded for hitting this speed and gains nothing by exceeding "
                         "it. 3.5 is why the RL athletes top out near 4.5 m/s while the heuristic bot "
                         "runs 9.")
    ap.add_argument("--target-speed-final", type=float, default=0.0,
                    help="if > 0, ramp the target speed from --target-speed to this over "
                         "--speed-ramp-iters. Asking a humanoid for sprint pace from a standing start "
                         "trains falling over; asking for it gradually trains running.")
    ap.add_argument("--speed-ramp-iters", type=int, default=1500,
                    help="iterations over which --target-speed reaches --target-speed-final. Ignored "
                         "when --speed-adaptive is set.")
    ap.add_argument("--speed-adaptive", action="store_true",
                    help="raise the target speed only while the athlete is staying on its feet, and "
                         "back off when it is not, instead of climbing on a fixed schedule. A clock "
                         "does not know where the body's limit is: ramping 4.0 -> 7.0 linearly walked "
                         "this rig off a cliff at about 5.5 m/s, where the fall rate went 0.04 -> 0.50 "
                         "in one window and episodes halved. This searches for the fastest pace the "
                         "policy can actually hold.")
    ap.add_argument("--speed-fall-low", type=float, default=0.05,
                    help="fall rate below which --speed-adaptive asks for more speed.")
    ap.add_argument("--speed-fall-high", type=float, default=0.15,
                    help="fall rate above which --speed-adaptive backs the target off.")
    ap.add_argument("--speed-step", type=float, default=0.05,
                    help="m/s the adaptive target moves per measured iteration.")
    ap.add_argument("--resume", default="")
    ap.add_argument("--save-every", type=int, default=50)
    ap.add_argument("--tb-port", type=int, default=6006)
    ap.add_argument("--lr", type=float, default=1e-3, help="initial PPO learning rate")
    ap.add_argument("--entropy-coef", type=float, default=0.005,
                    help="Entropy bonus. Raise it for a task with a comfortable local optimum to sit in; "
                         "the get-up policy will otherwise converge on lying down well.")
    ap.add_argument("--desired-kl", type=float, default=0.01,
                    help="KL the adaptive learning rate steers toward. Raise it for a task whose reward is "
                         "noisy enough that the controller otherwise pins the rate at its floor.")
    ap.add_argument("--no-domain-rand", action="store_true",
                    help="train on the bare MJCF: one friction, one mass set, one gain pair, no sensor "
                         "noise, no actuation latency. Policies trained this way overfit MuJoCo and do "
                         "not transfer to Unity's PhysX -- which is what happened to athlete_getup.onnx.")
    ap.add_argument("--dr-strength", type=float, default=1.0,
                    help="scales every randomisation range about its nominal value. 0 is equivalent to "
                         "--no-domain-rand; 2 doubles every spread.")
    ap.add_argument("--dr-ramp-iters", type=int, default=2000,
                    help="iterations over which randomisation ramps from --dr-start-strength to full. "
                         "Randomisation makes the task harder, and get-up at full strength from the "
                         "first step converges on lying still rather than standing (measured: return "
                         "23 -> 484 while stood fell 0.15 -> 0.07). Ramping lets the policy learn to "
                         "stand in near-nominal physics first and hardens it afterwards. 0 disables "
                         "the ramp.")
    ap.add_argument("--dr-start-strength", type=float, default=0.15,
                    help="randomisation strength at iteration 0, as a fraction of --dr-strength.")
    ap.add_argument("--air-time-cap", type=float, default=0.4,
                    help="seconds of flight a single footfall can be paid for by the air-time "
                         "reward. 0 removes the cap, which is what the term used to do and what "
                         "the policy learned to exploit: dive, stay airborne, collect. A human "
                         "sprint stride flies 0.10-0.20 s.")
    ap.add_argument("--lr-adapt", type=float, default=1.5,
                    help="multiplicative step of the KL-adaptive learning rate, applied per "
                         "minibatch. 1.5 is the rsl_rl default and thrashes at 40 minibatches "
                         "an iteration; 1.1 tracks the same target without the swing.")
    ap.add_argument("--epochs", type=int, default=5, help="PPO epochs per iteration")
    ap.add_argument("--gamma", type=float, default=0.99, help="PPO discount")
    ap.add_argument("--gae-lambda", type=float, default=0.95, help="GAE lambda")
    ap.add_argument("--clip", type=float, default=0.2, help="PPO clip range")
    ap.add_argument("--max-hours", type=float, default=0.0,
                    help="stop cleanly after this many hours, saving and exporting first. 0 = no "
                         "limit. Throughput moves with how often the bodies are falling, so an "
                         "iteration count is a poor way to ask for a fixed wall-clock budget; "
                         "this is the honest way to say 'train while I am out'.")
    ap.add_argument("--fall-penalty", type=float, default=2.0,
                    help="one-off reward cost of ending an episode fallen. Measured per-term on the "
                         "baseline this is worth -0.025 per step, against -0.324 for the foot-slip term: "
                         "falling was 13x cheaper than sliding a foot.")
    ap.add_argument("--track-var", type=float, default=2.0,
                    help="variance of the speed-tracking Gaussian. At the 2.0 default a standing body "
                         "scores exp(-3.5^2/2/2.0) = 0.0024 of the 1.5 on offer, with a gradient to "
                         "match, so nothing pulls it toward the commanded pace. Widen to restore it.")
    ap.add_argument("--action-scale", type=float, default=0.5,
                    help="radians of joint target per unit action. With the regenerated model's "
                         "stiffer joints this also scales the torque the exploration noise injects.")
    ap.add_argument("--init-std", type=float, default=0.8,
                    help="initial exploration standard deviation of the Gaussian policy.")
    ap.add_argument("--gait-w", type=float, default=0.0,
                    help="weight on matching a periodic contact schedule, and the switch that puts "
                         "the gait clock into the observation (obs 78 -> 80). 0 is off.")
    ap.add_argument("--gait-period", type=float, default=0.8, help="stride period in seconds")
    ap.add_argument("--gait-duty", type=float, default=0.6,
                    help="fraction of the cycle each foot is asked to be in stance. 0.6 leaves 20%% "
                         "of the stride in double support, which is a walk; below 0.5 is a run.")
    ap.add_argument("--spawn-facing", type=float, default=0.0,
                    help="fraction of episodes that start with the athlete already facing its "
                         "target, so walking does not have to be learned at the same time as "
                         "turning on the spot. 0 is the old uniformly random bearing.")
    ap.add_argument("--vel-gate", type=float, default=0.0,
                    help="fade the tracking and progress rewards out as the body drops or tilts, so "
                         "a topple toward the target stops being paid like a walk toward it. "
                         "0 off, 1 full.")
    ap.add_argument("--posture-w", type=float, default=1.0,
                    help="multiplier on alive+upright+heading, the income an athlete collects for "
                         "standing still and facing the target (0.79/step at 1.0).")
    ap.add_argument("--init-speed", type=float, default=0.0,
                    help="reset episodes already moving forward at U[0, this] m/s, so the policy "
                         "experiences being at speed instead of having to discover it first.")
    ap.add_argument("--alt-w", type=float, default=0.5,
                    help="weight on the double-support penalty. A run has no double-support phase, "
                         "but every walk does; 0 lets the athlete reach a walk before a run.")
    ap.add_argument("--prog-w", type=float, default=0.25,
                    help="weight on raw forward progress. Has to beat the 0.79/step that alive + "
                         "upright + heading pay for standing still, or standing still wins.")
    ap.add_argument("--keep-old-runs", action="store_true")
    ap.add_argument("--no-tensorboard", action="store_true")
    # Experiments export to scratch, NOT over the game's shipped policies.
    #
    # This default used to be Assets/Policies, so every run - including a 20-minute throwaway A/B -
    # overwrote athlete_run.onnx on every save. A 3-hour run that had learned to stand perfectly
    # still wrote itself into the shipped reference policy 22 times before anyone noticed, and the
    # only reason it was caught is that it showed up as a dirty file in git. Shipping a policy is a
    # decision, so it now takes a flag; the default cannot damage anything that is checked in.
    ap.add_argument("--unity-policies", default=os.path.join(HERE, "logs", "scratch_policies"))
    ap.add_argument("--publish", action="store_true",
                    help="Also copy the exported ONNX over the shipped Assets/Policies file. Only for "
                         "a run you have graded and actually want the game to use.")
    ap.add_argument("--run-name", default="")
    ap.add_argument("--task", choices=sorted(TASKS.keys()), default="target",
                    help="target = run to random targets; track = laps of the rooftop loop with a carrot "
                         "target; getup = recover to standing from a random fallen pose")
    args = ap.parse_args()

    global TASK
    TASK, env_cls, onnx_name = TASKS[args.task]
    torch.manual_seed(args.seed)
    device = "cuda"
    tb_root = os.path.join(HERE, "logs", "tb")
    if not args.keep_old_runs:
        clean_old_runs(tb_root, TASK)
    run_name = args.run_name or f"{TASK}_{time.strftime('%Y%m%d_%H%M%S')}"
    tb_dir = os.path.join(tb_root, run_name)
    writer = SummaryWriter(tb_dir)
    # Record what this run actually was. Two three-hour runs on 2026-09-13 left a CSV, a log and a
    # TensorBoard directory between them and no record of a single argument, so the only way to
    # recover their target speed was to read `env/target_speed` back out of the event files and the
    # rest was unrecoverable. A run whose configuration is not written down cannot be reproduced or
    # honestly compared against another.
    cfg_json = os.path.join(HERE, "logs", run_name + ".args.json")
    with open(cfg_json, "w", encoding="utf-8") as fh:
        json.dump({"argv": sys.argv[1:], "args": vars(args),
                   "started": time.strftime("%Y-%m-%d %H:%M:%S")}, fh, indent=2, sort_keys=True)
    print("[run] " + " ".join([os.path.basename(sys.argv[0])] + sys.argv[1:]))
    print(f"[run] config written to {cfg_json}")
    if not args.no_tensorboard:
        launch_tensorboard(tb_root, args.tb_port)

    domain_rand = not args.no_domain_rand and args.dr_strength > 0.0
    k = args.dr_strength
    dr_kwargs = {
        "friction": (1.0 - 0.3 * k, 1.0 + 0.3 * k),
        "mass": (1.0 - 0.1 * k, 1.0 + 0.1 * k),
        "kp": (1.0 - 0.2 * k, 1.0 + 0.25 * k),
        "kv": (1.0 - 0.2 * k, 1.0 + 0.25 * k),
        "obs_noise": k,
        "action_delay_prob": min(0.5, 0.15 * k),
        "push_vel": 0.6 * k,
    } if domain_rand else None
    env = env_cls(args.xml, args.num_envs, device=device, seed=args.seed, target_speed=args.target_speed,
                  domain_rand=domain_rand, dr_kwargs=dr_kwargs, cuda_graph=not args.no_cuda_graph,
                  air_time_cap=args.air_time_cap, fall_penalty=args.fall_penalty,
                  track_var=args.track_var, prog_w=args.prog_w, alt_w=args.alt_w,
                  posture_w=args.posture_w, init_speed=args.init_speed,
                  action_scale=args.action_scale, vel_gate=args.vel_gate,
                  spawn_facing=args.spawn_facing, gait_w=args.gait_w,
                  gait_period=args.gait_period, gait_duty=args.gait_duty)
    if domain_rand:
        ramp = (f"ramping {args.dr_start_strength:g} -> 1 over {args.dr_ramp_iters} iters"
                if args.dr_ramp_iters > 0 else "no ramp, full from iteration 0")
        print(f"[domain-rand] on, full strength {k:g} ({ramp}): friction x{dr_kwargs['friction']}, "
              f"mass x{dr_kwargs['mass']}, kp x{dr_kwargs['kp']}, kv x{dr_kwargs['kv']}, "
              f"obs noise {dr_kwargs['obs_noise']:g}, action delay p={dr_kwargs['action_delay_prob']:.2f}, "
              f"push {dr_kwargs['push_vel']:.2f} m/s -- these are the values at full strength")
    else:
        print("[domain-rand] OFF -- policy is fitted to MuJoCo exactly. That is a robustness choice, "
              "not a broken run: the get-up policy trained this way was measured getting a supine "
              "athlete up in Unity and holding the stand for 7.4 s of 8.")
    # Hold the minibatch near the 24k samples the old 4096-env default produced, so raising the
    # environment count buys throughput without quietly turning every gradient step into a much
    # coarser average over a much bigger batch.
    minibatches = args.minibatches or max(1, round(args.steps * args.num_envs / 24576))
    cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, desired_kl=args.desired_kl,
                    gamma=args.gamma, lam=args.gae_lambda, clip=args.clip,
                    entropy_coef=args.entropy_coef, minibatches=minibatches,
                    epochs=args.epochs, lr_adapt=args.lr_adapt, init_std=args.init_std)
    print(f"[ppo] batch {args.steps * args.num_envs:,} samples / iteration in {minibatches} minibatches "
          f"of {args.steps * args.num_envs // minibatches:,}")
    ppo = PPO(env.obs_dim, env.A, args.num_envs, device, cfg)
    # Per run, not per task. Every run used to write model_<iter>.pt into one directory shared by
    # the whole task, so a short A/B overwrote a long run's early checkpoints and three concurrent
    # A/Bs interleaved their saves into a single sequence that belonged to none of them. On
    # 2026-09-13 that put a 220-iteration experiment's weights under the same names as a
    # 7105-iteration run's, and only the high iteration numbers survived because the short run
    # never reached them. `latest.pt` was the same file for all of them.
    ck_dir = os.path.join(HERE, "checkpoints", TASK, run_name)
    os.makedirs(ck_dir, exist_ok=True)
    start_iter = 0
    if args.resume:
        extra = ppo.load(args.resume)
        start_iter = int(extra.get("iter", 0))
        print(f"resumed from {args.resume} at iter {start_iter}")

    # One CSV per run, named like the TensorBoard directory. The old shared "{TASK}.csv" was
    # appended to by every run of the task, so two runs in flight at once interleaved their rows
    # into one file with nothing to tell them apart -- and comparing an A against a B is the
    # whole point of keeping the CSV.
    csv_path = os.path.join(HERE, "logs", f"{run_name}.csv")
    if not os.path.exists(csv_path):
        open(csv_path, "w", encoding="utf-8").close()
    csv_f = open(csv_path, "a", newline="", encoding="utf-8")
    csv_w = csv.writer(csv_f)
    if os.path.getsize(csv_path) == 0:
        csv_w.writerow(["iter", "steps", "fps", "ep_return", "ep_len_s", "fall_rate", "v_toward", "reach_frac",
                        "kl", "lr", "std",
                        # KPI columns (envs/run_to_target._accumulate_kpis). Present for target/track;
                        # the get-up task leaves them 0, which is what "not measured here" looks like.
                        "surv_ratio", "v_err", "v_hit_frac", "torque", "power", "jerk",
                        "pitch_dev", "roll_dev", "act_sat", "duty_factor", "air_time", "foot_slip"]
                       + sorted(k for k in env.get_stats() if k.startswith("rt_")))

    obs = env.reset()
    total_steps = start_iter * args.steps * args.num_envs
    t_start = time.time()
    print(f"obs_dim={env.obs_dim} act_dim={env.A} envs={args.num_envs} control_dt={env.dt:.3f}s")
    for it in range(start_iter, args.iters):
        t0 = time.time()
        # Both curricula run over *this run's* iterations, not the checkpoint's absolute count. A
        # fine-tuning run resumed at iteration 2400 would otherwise start every ramp already finished,
        # which is precisely backwards: the reason to resume a competent policy is to harden it
        # gradually from where it is.
        run_it = it - start_iter
        if args.target_speed_final > 0.0 and not args.speed_adaptive:
            frac = 1.0 if args.speed_ramp_iters <= 0 else min(1.0, run_it / float(args.speed_ramp_iters))
            env.target_speed = (args.target_speed +
                                (args.target_speed_final - args.target_speed) * frac)
        if env.dr is not None:
            # Ramp the randomisation rather than applying it all at once; see --dr-ramp-iters.
            frac = 1.0 if args.dr_ramp_iters <= 0 else min(1.0, run_it / float(args.dr_ramp_iters))
            env.dr.set_strength(args.dr_strength *
                                (args.dr_start_strength + (1.0 - args.dr_start_strength) * frac))
            # The crowd task rides the same ramp: a soft pacemaker early, full contact later.
            if hasattr(env, "set_threat"):
                env.set_threat(args.dr_start_strength + (1.0 - args.dr_start_strength) * frac)
        with torch.no_grad():
            for _ in range(args.steps):
                act = ppo.act(obs)
                obs, rew, done, timeout = env.step(act)
                ppo.record(rew, done, timeout)
        stats = ppo.update(obs)
        total_steps += args.steps * args.num_envs
        fps = args.steps * args.num_envs / max(1e-6, time.time() - t0)
        s = env.get_stats()
        row = [it, total_steps, int(fps), s.get("ep_return", 0.0), s.get("ep_len_s", 0.0), s.get("fall_rate", 0.0),
               s.get("v_toward", 0.0), s.get("reach_frac", 0.0), stats["kl"], stats["lr"], stats["action_std"]]
        row += [s.get(k, 0.0) for k in ("surv_ratio", "v_err", "v_hit_frac", "torque", "power", "jerk",
                                        "pitch_dev", "roll_dev", "act_sat", "duty_factor", "air_time", "foot_slip")]
        row += [s.get(k, 0.0) for k in sorted(k for k in s if k.startswith("rt_"))]
        csv_w.writerow([f"{x:.4f}" if isinstance(x, float) else x for x in row]); csv_f.flush()
        for k, v in s.items():
            writer.add_scalar(f"env/{k}", v, it)
        for k, v in stats.items():
            writer.add_scalar(f"ppo/{k}", v, it)
        writer.add_scalar("perf/fps", fps, it)
        if env.dr is not None:
            writer.add_scalar("env/dr_strength", env.dr.strength, it)
        writer.add_scalar("env/target_speed", env.target_speed, it)

        # Adaptive speed curriculum. Only acts on iterations where episodes actually ended -- this task
        # runs 20 s episodes that reset in lockstep, so most iterations report no completed episode and
        # a fall rate of 0.0 that means "no data", not "nobody fell". Treating those as success would
        # ratchet the target up every step regardless of what the body is doing.
        if args.speed_adaptive and args.target_speed_final > 0.0 and s.get("ep_len_s", 0.0) > 0.0:
            fr = s.get("fall_rate", 0.0)
            if fr < args.speed_fall_low:
                env.target_speed = min(args.target_speed_final, env.target_speed + args.speed_step)
            elif fr > args.speed_fall_high:
                env.target_speed = max(args.target_speed, env.target_speed - args.speed_step)
        if it % 10 == 0:
            el = time.time() - t_start
            # The get-up task has nothing to run toward, so it prints what it is actually doing instead.
            middle = (f"| stood {s.get('stood_frac', 0):4.2f} | up {s.get('stand_frac', 0):4.2f} "
                      f"| hold {s.get('hold_frac', 0):4.2f}"
                      if TASK == "get_up" else
                      # Gait readouts sit next to speed on purpose: v_toward alone cannot tell a run
                      # from a fast shuffle, and duty/air/slip are what say which one is happening.
                      f"| fall {s.get('fall_rate', 0):4.2f} | v {s.get('v_toward', 0):5.2f} "
                      f"| duty {s.get('duty_factor', 0):4.2f} | air {s.get('air_time', 0):4.2f} "
                      f"| slip {s.get('foot_slip', 0):4.2f}")
            print(f"it {it:5d} | {fps:8.0f} sps | ret {s.get('ep_return', 0):7.2f} | len {s.get('ep_len_s', 0):5.1f}s "
                  f"{middle} "
                  f"| kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {el/60:5.1f} min", flush=True)
            if TASK != "get_up":
                # The KPI line. Return says a run is improving; these say whether it is improving
                # toward a humanoid that stays up, holds the pace and does not chatter.
                print(f"          surv {s.get('surv_ratio', 0):4.2f} | v_err {s.get('v_err', 0):5.2f} "
                      f"| hit {s.get('v_hit_frac', 0):4.2f} | tau {s.get('torque', 0):6.2f} Nm "
                      f"| pwr {s.get('power', 0):7.1f} W | jerk {s.get('jerk', 0):8.0f} "
                      f"| tilt {s.get('pitch_dev', 0):4.1f}/{s.get('roll_dev', 0):4.1f} deg "
                      f"| sat {s.get('act_sat', 0):4.2f}", flush=True)
        out_of_time = args.max_hours > 0.0 and (time.time() - t_start) >= args.max_hours * 3600.0
        if (it + 1) % args.save_every == 0 or it + 1 == args.iters or out_of_time:
            ck = os.path.join(ck_dir, f"model_{it + 1:05d}.pt")
            # action_scale and the target speed travel with the weights. `athlete_rollout.py`
            # otherwise reads action_scale out of athlete_policy_config.json, which is whatever the
            # model was last generated with -- so a checkpoint trained at 0.167 would be evaluated at
            # 0.5 and simply fall over, with nothing anywhere to say why.
            ppo.save(ck, {"iter": it + 1, "obs_dim": env.obs_dim, "act_dim": env.A,
                          "action_scale": env.action_scale, "target_speed": env.target_speed,
                          "run_name": run_name})
            shutil.copyfile(ck, os.path.join(ck_dir, "latest.pt"))
            onnx_path = os.path.join(ck_dir, "latest.onnx")
            export_onnx(ppo, onnx_path, env.obs_dim)
            os.makedirs(args.unity_policies, exist_ok=True)
            shutil.copyfile(onnx_path, os.path.join(args.unity_policies, onnx_name))
            write_policy_manifest(env, args, run_name,
                                  os.path.join(args.unity_policies, "athlete_policy_config.json"))
            dest = args.unity_policies
            if args.publish:
                pub = os.path.join(HERE, "..", "Assets", "Policies")
                os.makedirs(pub, exist_ok=True)
                shutil.copyfile(onnx_path, os.path.join(pub, onnx_name))
                dest += f" and Assets/Policies/{onnx_name}"
            print(f"saved {ck} and exported ONNX -> {dest}", flush=True)
        if out_of_time:
            print(f"[max-hours] reached {args.max_hours:g} h at iteration {it + 1}; stopping cleanly "
                  f"after {(time.time() - t_start) / 3600:.2f} h", flush=True)
            break
    writer.close()
    csv_f.close()


if __name__ == "__main__":
    main()
