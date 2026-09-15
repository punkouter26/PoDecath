"""Cross-check: feed the Unity-dumped observation to the same checkpoint Python-side."""
import os
import sys
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from ppo import PPO, PPOConfig, ExportPolicy  # noqa: E402

raw = open(os.path.join(HERE, "logs", "unity_obs_dump.csv")).read().strip().rstrip(",")
obs = np.array([float(x) for x in raw.split(",")], dtype=np.float32)
print(f"obs dim: {obs.shape[0]}")

dev = "cuda" if torch.cuda.is_available() else "cpu"
ppo = PPO(80, 21, 64, dev, PPOConfig())
extra = ppo.load(os.path.join(HERE, "checkpoints", "get_up", "getup_v80b", "latest.pt"))
print(f"checkpoint iter: {extra.get('iter')}")

policy = ExportPolicy(ppo.model, ppo.obs_rms, ppo.cfg.obs_clip).to(dev).eval()
o = torch.from_numpy(obs).unsqueeze(0).to(dev)
with torch.no_grad():
    a = policy(o)
a = a.cpu().numpy().reshape(-1)
print("python actions:", np.array2string(a, precision=3, suppress_small=True))
print("mean |a|:", float(np.abs(a).mean()))
print("saturated (|a|>=2.9):", int((np.abs(a) >= 2.9).sum()), "of", a.shape[0])
