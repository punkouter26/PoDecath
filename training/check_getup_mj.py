"""Roll the get-up checkpoint in the MuJoCo supine env; dump the lying observation."""
import math
import os
import sys
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from ppo import PPO, PPOConfig, ExportPolicy  # noqa: E402
from envs.get_up import GetUpEnv  # noqa: E402

dev = "cuda"
env = GetUpEnv(os.path.join(HERE, "models", "athlete.xml"), 256, device=dev,
               gait_w=1.0, gait_period=0.8, gait_duty=0.6, action_scale=0.167,
               domain_rand=False)
env.tilt_lo = math.pi      # force flat-supine resets: the probe's pose
env.tilt_hi = math.pi

ppo = PPO(80, 21, 256, dev, PPOConfig())
ppo.load(os.path.join(HERE, "checkpoints", "get_up", "getup_v80b", "latest.pt"))
pol = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip).to(dev).eval()

obs = env.reset()
first = obs[0].detach().cpu().numpy().copy()
np.save(os.path.join(HERE, "logs", "mj_supine_obs.npy"), first)

print("== MuJoCo supine sample, t=0 ==")
print("gravity  (6-8): ", np.array2string(first[6:9], precision=3))
print("command  (9-11):", np.array2string(first[9:12], precision=3))
print("feet     (75-76):", first[75:77])
print("base h   (77):  ", round(float(first[77]), 3))
print("gait     (78-79):", np.array2string(first[78:80], precision=3))

acts = []
upright0 = []
USE_UNITY_CLIP = True   # Unity clamps actions at actionClip=3 before driving; mirror that here
with torch.no_grad():
    for t in range(300):  # 6 s of control at 50 Hz
        a = pol(obs)
        raw = a.clone()
        if USE_UNITY_CLIP:
            a = a.clamp(-3.0, 3.0)
        acts.append(a[0].detach().cpu().numpy().copy())
        obs, _, done, timeout = env.step(a)
        upright0.append(float(-obs[0][8]))
acts = np.array(acts)
print("clip mode:", "±3 (Unity-faithful)" if USE_UNITY_CLIP else "unclamped")

print("\n== rollout (athlete 0) ==")
print("mean |a|:", round(float(np.abs(acts).mean()), 3),
      " max:", round(float(np.abs(acts).max()), 2))
print("upright at 1/2/4/6 s:", [round(upright0[i], 3) for i in (49, 99, 199, 299)])
stats = env.get_stats()
for k in ("stood_sum", "upright_sum", "hold_sum"):
    if k in stats:
        print(k, float(stats[k]))
