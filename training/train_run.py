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
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
TASKS = {  # name -> (env class, exported ONNX file name in Assets/Policies)
    "target": ("run_to_target", RunToTargetEnv, "athlete_run.onnx"),
    "track": ("run_track", RunTrackEnv, "athlete_track.onnx"),
    "getup": ("get_up", GetUpEnv, "athlete_getup.onnx"),
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


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--num-envs", type=int, default=4096)
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
                    help="iterations over which --target-speed reaches --target-speed-final.")
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
    ap.add_argument("--keep-old-runs", action="store_true")
    ap.add_argument("--no-tensorboard", action="store_true")
    ap.add_argument("--unity-policies", default=os.path.join(HERE, "..", "Assets", "Policies"))
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
                  domain_rand=domain_rand, dr_kwargs=dr_kwargs)
    if domain_rand:
        ramp = (f"ramping {args.dr_start_strength:g} -> 1 over {args.dr_ramp_iters} iters"
                if args.dr_ramp_iters > 0 else "no ramp, full from iteration 0")
        print(f"[domain-rand] on, full strength {k:g} ({ramp}): friction x{dr_kwargs['friction']}, "
              f"mass x{dr_kwargs['mass']}, kp x{dr_kwargs['kp']}, kv x{dr_kwargs['kv']}, "
              f"obs noise {dr_kwargs['obs_noise']:g}, action delay p={dr_kwargs['action_delay_prob']:.2f}, "
              f"push {dr_kwargs['push_vel']:.2f} m/s -- these are the values at full strength")
    else:
        print("[domain-rand] OFF -- policy will be fitted to MuJoCo exactly and is unlikely to transfer")
    cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, desired_kl=args.desired_kl,
                    entropy_coef=args.entropy_coef)
    ppo = PPO(env.obs_dim, env.A, args.num_envs, device, cfg)
    ck_dir = os.path.join(HERE, "checkpoints", TASK)
    os.makedirs(ck_dir, exist_ok=True)
    start_iter = 0
    if args.resume:
        extra = ppo.load(args.resume)
        start_iter = int(extra.get("iter", 0))
        print(f"resumed from {args.resume} at iter {start_iter}")

    csv_path = os.path.join(HERE, "logs", f"{TASK}.csv")
    csv_f = open(csv_path, "a", newline="", encoding="utf-8")
    csv_w = csv.writer(csv_f)
    if os.path.getsize(csv_path) == 0:
        csv_w.writerow(["iter", "steps", "fps", "ep_return", "ep_len_s", "fall_rate", "v_toward", "reach_frac", "kl", "lr", "std"])

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
        if args.target_speed_final > 0.0:
            frac = 1.0 if args.speed_ramp_iters <= 0 else min(1.0, run_it / float(args.speed_ramp_iters))
            env.target_speed = (args.target_speed +
                                (args.target_speed_final - args.target_speed) * frac)
        if env.dr is not None:
            # Ramp the randomisation rather than applying it all at once; see --dr-ramp-iters.
            frac = 1.0 if args.dr_ramp_iters <= 0 else min(1.0, run_it / float(args.dr_ramp_iters))
            env.dr.set_strength(args.dr_strength *
                                (args.dr_start_strength + (1.0 - args.dr_start_strength) * frac))
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
        csv_w.writerow([f"{x:.4f}" if isinstance(x, float) else x for x in row]); csv_f.flush()
        for k, v in s.items():
            writer.add_scalar(f"env/{k}", v, it)
        for k, v in stats.items():
            writer.add_scalar(f"ppo/{k}", v, it)
        writer.add_scalar("perf/fps", fps, it)
        if env.dr is not None:
            writer.add_scalar("env/dr_strength", env.dr.strength, it)
        writer.add_scalar("env/target_speed", env.target_speed, it)
        if it % 10 == 0:
            el = time.time() - t_start
            # The get-up task has nothing to run toward, so it prints what it is actually doing instead.
            middle = (f"| stood {s.get('stood_frac', 0):4.2f} | up {s.get('stand_frac', 0):4.2f} "
                      f"| hold {s.get('hold_frac', 0):4.2f}"
                      if TASK == "get_up" else
                      f"| fall {s.get('fall_rate', 0):4.2f} | v {s.get('v_toward', 0):5.2f} "
                      f"| reach {s.get('reach_frac', 0):.3f}")
            print(f"it {it:5d} | {fps:8.0f} sps | ret {s.get('ep_return', 0):7.2f} | len {s.get('ep_len_s', 0):5.1f}s "
                  f"{middle} "
                  f"| kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {el/60:5.1f} min", flush=True)
        if (it + 1) % args.save_every == 0 or it + 1 == args.iters:
            ck = os.path.join(ck_dir, f"model_{it + 1:05d}.pt")
            ppo.save(ck, {"iter": it + 1, "obs_dim": env.obs_dim, "act_dim": env.A})
            shutil.copyfile(ck, os.path.join(ck_dir, "latest.pt"))
            onnx_path = os.path.join(ck_dir, "latest.onnx")
            export_onnx(ppo, onnx_path, env.obs_dim)
            os.makedirs(args.unity_policies, exist_ok=True)
            shutil.copyfile(onnx_path, os.path.join(args.unity_policies, onnx_name))
            print(f"saved {ck} and exported ONNX -> Assets/Policies/{onnx_name}", flush=True)
    writer.close()
    csv_f.close()


if __name__ == "__main__":
    main()
