"""Train the run-to-target policy in Isaac Lab (PhysX GPU) with rsl_rl PPO, matched to train_run.py.

    .venv/Scripts/python.exe train_isaac.py --num-envs 4096 --iters 1500 --headless

Exports Assets/Policies/athlete_isaac.onnx (normaliser baked in, same 75-in / 21-out contract) and logs to
training/logs/tb/isaac_run_to_target_<time> so both trainers show up side by side in TensorBoard (port 6006).
House rules: TensorBoard is launched, stale isaac_* runs are removed first.
"""
from __future__ import annotations

import argparse
import csv
import os
import shutil
import subprocess
import sys
import time

from isaaclab.app import AppLauncher

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
parser = argparse.ArgumentParser()
parser.add_argument("--num-envs", type=int, default=4096)
parser.add_argument("--iters", type=int, default=1500)
parser.add_argument("--steps", type=int, default=24)
parser.add_argument("--seed", type=int, default=0)
parser.add_argument("--target-speed", type=float, default=3.5)
parser.add_argument("--tb-port", type=int, default=6006)
parser.add_argument("--keep-old-runs", action="store_true")
parser.add_argument("--no-tensorboard", action="store_true")
parser.add_argument("--resume", default="")
AppLauncher.add_app_launcher_args(parser)
args = parser.parse_args()
app_launcher = AppLauncher(args)
simulation_app = app_launcher.app

import torch  # noqa: E402
from rsl_rl.runners import OnPolicyRunner  # noqa: E402
from isaaclab_rl.rsl_rl import RslRlOnPolicyRunnerCfg, RslRlPpoActorCriticCfg, RslRlPpoAlgorithmCfg, RslRlVecEnvWrapper  # noqa: E402

sys.path.insert(0, HERE)
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg  # noqa: E402

TASK = "isaac_run_to_target"


def launch_tensorboard(logdir: str, port: int) -> None:
    try:
        subprocess.Popen([sys.executable, "-m", "tensorboard.main", "--logdir", logdir, "--port", str(port), "--bind_all"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
        print(f"[tensorboard] http://localhost:{port}  (logdir {logdir})")
    except Exception as e:
        print(f"[tensorboard] failed: {e}")


def clean_old_runs(tb_root: str) -> None:
    if not os.path.isdir(tb_root):
        return
    for name in os.listdir(tb_root):
        p = os.path.join(tb_root, name)
        if os.path.isdir(p) and name.startswith(TASK):
            shutil.rmtree(p, ignore_errors=True)
            print(f"[tensorboard] removed obsolete run {name}")


def export_onnx(runner: OnPolicyRunner, path: str) -> None:
    """Export actor + observation normaliser with IsaacLab's exporter: obs(1,75) -> actions(1,21)."""
    from isaaclab_rl.rsl_rl import export_policy_as_onnx

    policy_nn = runner.alg.policy
    normalizer = getattr(policy_nn, "actor_obs_normalizer", None)
    export_policy_as_onnx(policy_nn, path=os.path.dirname(path), normalizer=normalizer, filename=os.path.basename(path))
    policy_nn.to(runner.device)


def main() -> None:
    tb_root = os.path.join(ROOT, "logs", "tb")
    if not args.keep_old_runs:
        clean_old_runs(tb_root)
    run_name = f"{TASK}_{time.strftime('%Y%m%d_%H%M%S')}"
    log_dir = os.path.join(tb_root, run_name)
    os.makedirs(log_dir, exist_ok=True)
    if not args.no_tensorboard:
        launch_tensorboard(tb_root, args.tb_port)

    env_cfg = RunToTargetIsaacEnvCfg()
    env_cfg.scene.num_envs = args.num_envs
    env_cfg.target_speed = args.target_speed
    env_cfg.seed = args.seed
    env = RunToTargetIsaacEnv(env_cfg, render_mode=None)
    env = RslRlVecEnvWrapper(env)

    agent_cfg = RslRlOnPolicyRunnerCfg(
        seed=args.seed,
        device="cuda:0",
        num_steps_per_env=args.steps,
        max_iterations=args.iters,
        save_interval=50,
        experiment_name=TASK,
        obs_groups={"policy": ["policy"], "critic": ["policy"]},
        policy=RslRlPpoActorCriticCfg(init_noise_std=0.8, actor_hidden_dims=[512, 256, 128],
                                      critic_hidden_dims=[512, 256, 128], activation="elu",
                                      actor_obs_normalization=True, critic_obs_normalization=True),
        algorithm=RslRlPpoAlgorithmCfg(value_loss_coef=1.0, use_clipped_value_loss=True, clip_param=0.2,
                                       entropy_coef=0.005, num_learning_epochs=5, num_mini_batches=4,
                                       learning_rate=1e-3, schedule="adaptive", gamma=0.99, lam=0.95,
                                       desired_kl=0.01, max_grad_norm=1.0),
    )
    runner = OnPolicyRunner(env, agent_cfg.to_dict(), log_dir=log_dir, device="cuda:0")
    if args.resume:
        runner.load(args.resume)

    t0 = time.time()
    runner.learn(num_learning_iterations=args.iters, init_at_random_ep_len=True)
    wall = time.time() - t0

    ck_dir = os.path.join(ROOT, "checkpoints", TASK)
    os.makedirs(ck_dir, exist_ok=True)
    runner.save(os.path.join(ck_dir, "latest.pt"))
    onnx_path = os.path.join(ck_dir, "latest.onnx")
    export_onnx(runner, onnx_path)
    unity = os.path.abspath(os.path.join(ROOT, "..", "Assets", "Policies"))
    os.makedirs(unity, exist_ok=True)
    shutil.copyfile(onnx_path, os.path.join(unity, "athlete_isaac.onnx"))
    steps = args.iters * args.steps * args.num_envs
    with open(os.path.join(ROOT, "logs", "isaac_run_to_target.csv"), "a", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow([run_name, args.iters, steps, f"{wall:.0f}", f"{steps / wall:.0f}"])
    print(f"done: {args.iters} iters, {steps/1e6:.0f}M steps in {wall/60:.1f} min ({steps/wall:.0f} sps) -> Assets/Policies/athlete_isaac.onnx")


if __name__ == "__main__":
    main()
    simulation_app.close()
