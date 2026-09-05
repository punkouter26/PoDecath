import argparse, faulthandler, sys; faulthandler.enable()
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
def probe(name):
    print("PROBE importing", name, flush=True); __import__(name); print("PROBE ok", name, flush=True)
for m in ["torch", "tensorboard", "rsl_rl", "rsl_rl.runners", "isaaclab_rl", "isaaclab_rl.rsl_rl", "onnxscript"]:
    probe(m)
print("PROBE all imports ok", flush=True)
app.close()
