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
from envs.run_to_target import RunToTargetEnv  # noqa: E402
from envs.run_track import RunTrackEnv  # noqa: E402
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
TASKS = {  # name -> (env class, exported ONNX file name in Assets/Policies)
    "target": ("run_to_target", RunToTargetEnv, "athlete_run.onnx"),
    "track": ("run_track", RunTrackEnv, "athlete_track.onnx"),
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
    ap.add_argument("--target-speed", type=float, default=3.5)
    ap.add_argument("--resume", default="")
    ap.add_argument("--save-every", type=int, default=50)
    ap.add_argument("--tb-port", type=int, default=6006)
    ap.add_argument("--keep-old-runs", action="store_true")
    ap.add_argument("--no-tensorboard", action="store_true")
    ap.add_argument("--unity-policies", default=os.path.join(HERE, "..", "Assets", "Policies"))
    ap.add_argument("--run-name", default="")
    ap.add_argument("--task", choices=sorted(TASKS.keys()), default="target",
                    help="target = run to random targets; track = laps of the rooftop loop with a carrot target")
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

    env = env_cls(args.xml, args.num_envs, device=device, seed=args.seed, target_speed=args.target_speed)
    cfg = PPOConfig(steps_per_env=args.steps)
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
        if it % 10 == 0:
            el = time.time() - t_start
            print(f"it {it:5d} | {fps:8.0f} sps | ret {s.get('ep_return', 0):7.2f} | len {s.get('ep_len_s', 0):5.1f}s "
                  f"| fall {s.get('fall_rate', 0):4.2f} | v {s.get('v_toward', 0):5.2f} | reach {s.get('reach_frac', 0):.3f} "
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
