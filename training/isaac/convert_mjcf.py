"""Convert training/models/athlete.xml (the same MJCF MuJoCo Warp trains on) to USD for Isaac Lab.

    .venv/Scripts/python.exe convert_mjcf.py            # writes training/isaac/usd/athlete.usd

Keeping one source model is what makes the MuJoCo-vs-Isaac comparison fair: same masses, limits,
joint order (by name) and PD gains (applied through ImplicitActuatorCfg in the task).
"""
from __future__ import annotations

import argparse
import os

from isaaclab.app import AppLauncher

HERE = os.path.dirname(os.path.abspath(__file__))
parser = argparse.ArgumentParser()
# The chained variant (one hinge per body) is required: the importer maps multi-joint bodies onto D6 joints
# with PhysX's fixed X/Y/Z axis order, which swaps the abdomen axes and bends the shoulder sideways.
parser.add_argument("--mjcf", default=os.path.join(HERE, "..", "models", "athlete_chain.xml"))
parser.add_argument("--out", default=os.path.join(HERE, "usd"))
AppLauncher.add_app_launcher_args(parser)
args = parser.parse_args()
args.headless = True
app_launcher = AppLauncher(args)
simulation_app = app_launcher.app

from isaacsim.core.utils.extensions import enable_extension  # noqa: E402

enable_extension("isaacsim.asset.importer.mjcf")   # registers MJCFCreateImportConfig / MJCFCreateAsset
import isaacsim.asset.importer.mjcf  # noqa: E402,F401

from isaaclab.sim.converters import MjcfConverter, MjcfConverterCfg  # noqa: E402


def main() -> None:
    cfg = MjcfConverterCfg(
        asset_path=os.path.abspath(args.mjcf),
        usd_dir=os.path.abspath(args.out),
        usd_file_name="athlete.usd",
        force_usd_conversion=True,
        make_instanceable=True,
        fix_base=False,
        import_sites=False,
        self_collision=False,
    )
    conv = MjcfConverter(cfg)
    print(f"USD written: {conv.usd_path}")


if __name__ == "__main__":
    main()
    simulation_app.close()
