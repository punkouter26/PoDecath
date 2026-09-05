import argparse, faulthandler; faulthandler.enable()
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
for m in ["torch", "tensordict", "torchvision", "rsl_rl.utils", "rsl_rl.storage", "rsl_rl.modules", "rsl_rl.algorithms", "rsl_rl.runners"]:
    print("PROBE importing", m, flush=True)
    try: __import__(m); print("PROBE ok", m, flush=True)
    except ImportError as e: print("PROBE importerror", m, e, flush=True)
app.close()
