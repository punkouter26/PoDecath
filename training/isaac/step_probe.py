import argparse, sys, os
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg, JOINT_ORDER
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 1
env = RunToTargetIsaacEnv(cfg); env.reset(); r = env.robot
print("ISAAC drive stiffness (policy order, first 12):", [round(x,1) for x in r.data.joint_stiffness[0, env.jidx][:12].tolist()], flush=True)
print("ISAAC drive damping:", [round(x,1) for x in r.data.joint_damping[0, env.jidx][:12].tolist()], flush=True)
print("ISAAC effort limits:", [round(x,1) for x in r.data.joint_effort_limits[0, env.jidx][:12].tolist()], flush=True)
print("ISAAC armature:", [round(x,3) for x in r.data.joint_armature[0, env.jidx][:6].tolist()], " masses:", [round(x,2) for x in r.data.default_mass[0].tolist()], flush=True)
# hold the body in the air (immovable-ish) by resetting root each step, apply a +0.3 rad step on hip_y_l, log the joint over 0.2 s
hip = env.jidx[JOINT_ORDER.index("hip_y_l")].item()
jp0 = r.data.default_joint_pos.clone(); tgt = jp0.clone(); tgt[0, hip] += 0.3
traj = []
for i in range(40):
    r.write_root_pose_to_sim(torch.tensor([[0,0,1.5,1,0,0,0]], device=env.device, dtype=torch.float32)); r.write_root_velocity_to_sim(torch.zeros(1,6, device=env.device))
    if i == 0: r.write_joint_state_to_sim(jp0, torch.zeros_like(jp0))
    r.set_joint_position_target(tgt); r.write_data_to_sim(); env.sim.step(render=False); r.update(0.005)
    traj.append(round(r.data.joint_pos[0, hip].item() - jp0[0, hip].item(), 4))
print("ISAAC hip_y_l step response (rad, every 5 ms):", traj[:40], flush=True)
app.close()
