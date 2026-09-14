"""Can the athlete physically take a step at a given action scale?

Lowering `--action-scale` from 0.5 to 0.167 is what stopped the exploration noise knocking the body
over, and it buys that by shrinking how far the policy can move a joint: the action is clipped at
+-3, so the reachable joint target is +-3 * action_scale radians from the stand pose. At 0.167 that
is +-0.5 rad (29 degrees) on every joint. A walking stride wants roughly 0.6 rad of hip flexion and
1.0 rad of knee flexion, so the fix for one problem may have made the goal unreachable.

This drives an open-loop stepping motion -- a sinusoid on one hip and knee, the shape a walk needs --
at several action scales, and reports how far the swing foot actually leaves the ground and whether
the body survives it. No policy, no learning: purely what the actuators can do.
"""
import argparse
import os

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ACTION_CLIP = 3.0


def try_scale(m, scale, period=0.8, seconds=4.0, amp=3.0):
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    default = d.qpos[7:].copy()
    stand = float(d.qpos[2])
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
    names = [mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_ACTUATOR, i) for i in range(m.nu)]
    idx = {n: i for i, n in enumerate(names)}
    lo, hi = m.actuator_ctrlrange[:, 0], m.actuator_ctrlrange[:, 1]
    geoms = [mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_%s_geom" % s) for s in "lr"]
    half = m.geom_size[geoms[0]]

    peak_clear, fell_at, dt = 0.0, None, 0.02
    while d.time < seconds:
        ph = 2 * np.pi * d.time / period
        a = np.zeros(m.nu)
        # left leg swings while the right supports, and back again half a cycle later
        a[idx["hip_y_l"]] = -amp * np.sin(ph)     # negative hip_y is flexion (range -120..30 deg)
        a[idx["knee_l"]] = amp * max(0.0, np.sin(ph))
        a[idx["hip_y_r"]] = -amp * np.sin(ph + np.pi)
        a[idx["knee_r"]] = amp * max(0.0, np.sin(ph + np.pi))
        a = np.clip(a, -ACTION_CLIP, ACTION_CLIP)
        d.ctrl[:] = np.clip(default + a * scale, lo, hi)
        for _ in range(4):
            mujoco.mj_step(m, d)
        cz = d.geom_xpos[geoms][:, 2]
        rz = d.geom_xmat[geoms].reshape(-1, 3, 3)[:, 2, :]
        sole_z = cz - (np.abs(rz) * half).sum(-1)
        peak_clear = max(peak_clear, float(sole_z.max()))
        if d.xpos[pelvis][2] < stand * 0.6:
            fell_at = d.time
            break
    return peak_clear, fell_at


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    a = ap.parse_args()
    m = mujoco.MjModel.from_xml_path(a.xml)
    print("Open-loop stepping motion, no policy. 'clearance' is the highest the swing foot's lowest")
    print("corner gets off the deck; a walk needs roughly 0.05-0.10 m.")
    print("")
    print("%12s %14s %12s %10s" % ("action_scale", "joint travel", "clearance", "fell at"))
    for scale in (0.1, 0.167, 0.25, 0.35, 0.5):
        clear, fell = try_scale(m, scale)
        print("%12.3f %11.2f rad %11.3f m %10s"
              % (scale, ACTION_CLIP * scale, clear, ("%.2fs" % fell) if fell else "stayed up"))
