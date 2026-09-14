"""Which joint lets go when the athlete falls out of its own stand pose?

Holds the keyframe joint targets and prints, per 0.25 s, the pelvis pitch, the CoM position over the
foot, and the joints whose measured angle has drifted furthest from the target the PD is holding.
Tracking error and actuator saturation together say whether a joint is too weak to hold the pose or
is being commanded into it.
"""
import os
import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
m = mujoco.MjModel.from_xml_path(os.path.join(HERE, "models", "athlete.xml"))
d = mujoco.MjData(m)
mujoco.mj_resetDataKeyframe(m, d, 0)
mujoco.mj_forward(m, d)
names = [mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_ACTUATOR, i) for i in range(m.nu)]
target = d.qpos[7:].copy()
d.ctrl[:] = target
pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")

print(f"{'t':>5} {'pitch':>7} {'CoM_x':>7} {'supp%':>7}   worst joint tracking errors (rad, [force/limit])")
for k in range(13):
    t_s = k * 0.25
    while d.time < t_s:
        mujoco.mj_step(m, d)
    mujoco.mj_forward(m, d)
    q = d.xquat[pelvis]
    w, x, y, z = q
    pitch = np.degrees(np.arcsin(np.clip(2 * (w * y - z * x), -1, 1)))
    err = d.qpos[7:] - target
    order = np.argsort(-np.abs(err))[:4]
    lo, hi = [], []
    for s in "lr":
        g = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, f"foot_{s}_geom")
        c, R, half = d.geom_xpos[g], d.geom_xmat[g].reshape(3, 3), m.geom_size[g]
        cor = np.array([c + R @ (np.array([sx, sy, sz]) * half)
                        for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)])
        lo.append(cor[:, 0].min()); hi.append(cor[:, 0].max())
    x_lo, x_hi = min(lo), max(hi)
    com = d.subtree_com[0]
    frac = (com[0] - x_lo) / (x_hi - x_lo) * 100
    worst = "  ".join(f"{names[i]} {err[i]:+.3f} [{abs(d.actuator_force[i]) / m.actuator_forcerange[i][1]:.2f}]"
                      for i in order)
    print(f"{d.time:5.2f} {pitch:+7.1f} {com[0]:+7.3f} {frac:7.1f}   {worst}")
