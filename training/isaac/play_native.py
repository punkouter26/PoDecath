import argparse, sys, os
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch, numpy as np
from rsl_rl.runners import OnPolicyRunner
from isaaclab_rl.rsl_rl import RslRlOnPolicyRunnerCfg, RslRlPpoActorCriticCfg, RslRlPpoAlgorithmCfg, RslRlVecEnvWrapper
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 1
env = RunToTargetIsaacEnv(cfg); wenv = RslRlVecEnvWrapper(env)
agent = RslRlOnPolicyRunnerCfg(seed=0, device="cuda:0", num_steps_per_env=24, max_iterations=1, save_interval=50, experiment_name="probe",
    obs_groups={"policy": ["policy"], "critic": ["policy"]},
    policy=RslRlPpoActorCriticCfg(init_noise_std=0.8, actor_hidden_dims=[512,256,128], critic_hidden_dims=[512,256,128], activation="elu", actor_obs_normalization=True, critic_obs_normalization=True),
    algorithm=RslRlPpoAlgorithmCfg(value_loss_coef=1.0, use_clipped_value_loss=True, clip_param=0.2, entropy_coef=0.005, num_learning_epochs=5, num_mini_batches=4, learning_rate=1e-3, schedule="adaptive", gamma=0.99, lam=0.95, desired_kl=0.01, max_grad_norm=1.0))
runner = OnPolicyRunner(wenv, agent.to_dict(), log_dir=None, device="cuda:0")
runner.load(os.path.abspath("../checkpoints/isaac_run_to_target/latest.pt"))
policy = runner.get_inference_policy(device="cuda:0")
r = env.robot
env.reset()
r.write_root_pose_to_sim(torch.tensor([[0,0,0.95,1,0,0,0]], device=env.device, dtype=torch.float32)); r.write_root_velocity_to_sim(torch.zeros(1,6, device=env.device))
r.write_joint_state_to_sim(r.data.default_joint_pos.clone(), torch.zeros_like(r.data.default_joint_pos))
env.targets[:] = torch.tensor([[108.0, 0.0]], device=env.device)
env.sim.step(render=False); r.update(0.005); obs = env._get_observations()["policy"]
O, A = [], []
for k in range(8):
    with torch.no_grad(): act = policy({"policy": obs})
    o = obs[0].cpu().numpy(); ac = act[0].cpu().numpy(); O.append(o); A.append(ac)
    print("NATIVE step", k, "obs[0:12]=", np.round(o[:12],3).tolist(), "jvel[0:4]=", np.round(o[33:37],2).tolist(), "act[0:6]=", np.round(ac[:6],2).tolist(), "pelvis=", [round(x,3) for x in r.data.root_pos_w[0].tolist()], flush=True)
    obs, rew, term, trunc, info = env.step(act); obs = obs["policy"]
np.savez("logs/native_trace.npz", obs=np.stack(O), act=np.stack(A))
print("NATIVE saved", flush=True)
app.close()
