"""Is this body statically balanced in its own stand pose, and where is its weight?

Holds the default joint targets for a few seconds in plain MuJoCo and reports the centre of mass
against the support polygon the two foot boxes actually make. A body whose CoM sits near the toe
edge of its own support is one ankle torque away from pitching forward, and no amount of reward
shaping fixes that.
"""
import os, sys
import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
m = mujoco.MjModel.from_xml_path(os.path.join(HERE, "models", "athlete.xml"))
d = mujoco.MjData(m)
mujoco.mj_resetDataKeyframe(m, d, 0)
mujoco.mj_forward(m, d)

print(f"total mass {sum(m.body_mass):.2f} kg, gravity {m.opt.gravity[2]:.2f} m/s2, "
      f"timestep {m.opt.timestep}, {m.nu} actuators")

pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
d.ctrl[:] = d.qpos[7:]
stand_h = float(d.qpos[2])


def foot_extent():
    """World-frame x range of both foot boxes (the support polygon along the direction of travel)."""
    lo, hi, zs = [], [], []
    for s in "lr":
        g = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, f"foot_{s}_geom")
        c = d.geom_xpos[g]
        R = d.geom_xmat[g].reshape(3, 3)
        half = m.geom_size[g]
        corners = [c + R @ (np.array([sx, sy, sz]) * half)
                   for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
        corners = np.array(corners)
        lo.append(corners[:, 0].min()); hi.append(corners[:, 0].max()); zs.append(corners[:, 2].min())
    return min(lo), max(hi), min(zs)


for t_s in (0.0, 0.5, 1.0, 2.0, 3.0):
    while d.time < t_s:
        mujoco.mj_step(m, d)
    mujoco.mj_forward(m, d)
    com = d.subtree_com[0]
    x_lo, x_hi, z_lo = foot_extent()
    span = x_hi - x_lo
    frac = (com[0] - x_lo) / span if span > 0 else float("nan")
    grav_b = np.array([0, 0, -1.0])
    q = d.xquat[pelvis]
    w, x, y, z = q
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                  [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                  [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])
    upright = -(R.T @ grav_b)[2]
    print(f"t={d.time:4.1f}s  pelvis_z {d.xpos[pelvis][2]:.3f} (stand {stand_h:.3f})  upright {upright:.3f}  "
          f"CoM_x {com[0]:+.3f}  foot_x [{x_lo:+.3f}, {x_hi:+.3f}]  CoM at {frac * 100:5.1f}% heel->toe  "
          f"sole_z {z_lo:+.4f}")

print("\n0% = heel edge, 100% = toe edge. A standing human sits around 40-50%.")
