import argparse, sys, os
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 1; cfg.events = None
env = RunToTargetIsaacEnv(cfg); env.reset(); r = env.robot; names = r.body_names
def fk(offsets):
    jp = r.data.default_joint_pos.clone()
    for n, v in offsets.items(): jp[0, r.joint_names.index(n)] += v
    for _ in range(2):
        r.write_root_pose_to_sim(torch.tensor([[0,0,1.5,1,0,0,0]], device=env.device, dtype=torch.float32)); r.write_root_velocity_to_sim(torch.zeros(1,6, device=env.device))
        r.write_joint_state_to_sim(jp, torch.zeros_like(jp)); r.set_joint_position_target(jp); r.write_data_to_sim(); env.sim.step(render=False); r.update(0.005)
    Q = lambda b: [round(x,3) for x in r.data.body_quat_w[0, names.index(b)].tolist()]
    return Q("torso"), Q("forearm_l"), Q("foot_l")
cases = [("abdomen_z", 0.5), ("abdomen_y", 0.5), ("abdomen_x", 0.5), ("hip_x_l", 0.5), ("hip_z_l", 0.5), ("hip_y_l", 0.5), ("knee_l", 0.5), ("ankle_y_l", 0.5), ("ankle_x_l", 0.5), ("shoulder_x_l", 0.5), ("shoulder_z_l", 0.5), ("elbow_l", -0.5)]
for name, off in cases:
    t, h, f = fk({name: off}); print("IS %-13s torso %s forearm_l %s foot_l %s" % (name, t, h, f), flush=True)
t, h, f = fk({"hip_x_l": 0.3, "hip_z_l": 0.3, "hip_y_l": -0.5}); print("IS combined hip: foot_l quat", f, flush=True)
print("IS done", flush=True)
app.close()
