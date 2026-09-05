import argparse, sys, os, math
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch, numpy as np, onnxruntime as ort
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 1
env = RunToTargetIsaacEnv(cfg); obs, _ = env.reset()
# deterministic start: no yaw, target 100 m ahead along +x
r = env.robot
r.write_root_pose_to_sim(torch.tensor([[0,0,0.95,1,0,0,0]], device=env.device, dtype=torch.float32))
r.write_root_velocity_to_sim(torch.zeros(1,6, device=env.device))
r.write_joint_state_to_sim(r.data.default_joint_pos.clone(), torch.zeros_like(r.data.default_joint_pos))
env.targets[:] = torch.tensor([[108.0, 0.0]], device=env.device)
env.sim.step(render=False); r.update(0.005); obs = env._get_observations()["policy"]
sess = ort.InferenceSession(os.path.abspath("../../Assets/Policies/athlete_isaac.onnx"))
for k in range(8):
    o = obs[0].cpu().numpy().astype(np.float32)
    act = sess.run(None, {"obs": o[None]})[0][0]
    print("ISAAC step", k, "obs[0:12]=", np.round(o[:12],3).tolist(), "jvel[0:4]=", np.round(o[33:37],2).tolist(), "act[0:6]=", np.round(act[:6],2).tolist(), "pelvis=", [round(x,3) for x in r.data.root_pos_w[0].tolist()], flush=True)
    obs, rew, term, trunc, info = env.step(torch.tensor(act[None], device=env.device))
    obs = obs["policy"]
app.close()
