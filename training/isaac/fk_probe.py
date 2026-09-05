import argparse, sys, os
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
import torch
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tasks.run_to_target_isaac import RunToTargetIsaacEnv, RunToTargetIsaacEnvCfg, JOINT_ORDER, DEFAULTS
cfg = RunToTargetIsaacEnvCfg(); cfg.scene.num_envs = 1
env = RunToTargetIsaacEnv(cfg); env.reset()
r = env.robot; names = r.body_names
def fk(offsets):
    jp = r.data.default_joint_pos.clone()
    for n, v in offsets.items():
        i = r.joint_names.index(n); jp[0, i] += v
    r.write_root_pose_to_sim(torch.tensor([[0,0,0.93,1,0,0,0]], device=env.device, dtype=torch.float32))
    r.write_joint_state_to_sim(jp, torch.zeros_like(jp)); env.sim.step(render=False); r.update(0.005)
    pel = r.data.body_pos_w[0, names.index("pelvis")]
    return {b: [round(x,3) for x in (r.data.body_pos_w[0, names.index(b)] - pel).tolist()] for b in ["torso","forearm_l","foot_l","foot_r","forearm_r"]}
print("ISAAC default:", fk({}), flush=True)
print("ISAAC hip_y_l+0.5:", fk({"hip_y_l": 0.5})["foot_l"], " shoulder_x_l-0.5:", fk({"shoulder_x_l": -0.5})["forearm_l"], " abdomen_y+0.5 torso:", fk({"abdomen_y": 0.5})["torso"], " knee_l+0.5:", fk({"knee_l": 0.5})["foot_l"], flush=True)
app.close()
