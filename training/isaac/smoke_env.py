import argparse, sys, os, time
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg, JOINT_ORDER
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 256
env = RunToTargetIsaacEnv(cfg)
print("SMOKE sim joint names:", env.robot.joint_names)
print("SMOKE policy order idx:", env.jidx.tolist())
obs, _ = env.reset()
o = obs["policy"]; print("SMOKE obs shape", tuple(o.shape), "finite", torch.isfinite(o).all().item(), "grav", o[0,6:9].tolist(), "cmd", o[0,9:12].tolist())
t = time.time(); n = 50
for i in range(n):
    obs, rew, term, trunc, info = env.step(torch.zeros(cfg.scene.num_envs, env.jidx.numel(), device=env.device))
torch.cuda.synchronize(); dt = time.time() - t
print("SMOKE %d steps: %.0f control sps, reward mean %.3f, terminated frac %.3f, height %.3f" % (n, n*cfg.scene.num_envs/dt, rew.mean().item(), term.float().mean().item(), env.robot.data.root_pos_w[:,2].mean().item()))
env.close(); app.close()
