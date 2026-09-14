"""Can this body hold its own stand pose at all, and does more joint stiffness fix it?

An inverted pendulum of mass M whose centre of mass sits h above the ankle needs an ankle stiffness
of at least M*g*h to be even marginally stable. For this athlete that floor is 75 * 9.81 * 0.9 =
662 N m/rad against the 250 the model ships -- but the measured topple is not an ankle failure: the
ankle never gets past 20 N m of its 220 N m limit. The lean is spread across hip, knee and ankle,
each deflecting a little, so this sweeps a multiplier on every leg and trunk gain at once.

kv is scaled with kp so the damping ratio stays where it was; scaling stiffness alone would turn a
stiffer joint into an oscillator and measure that instead.
"""
import os
import re
import tempfile

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "models", "athlete.xml"), encoding="utf-8").read()

LEG_TRUNK = ("hip_x", "hip_z", "hip_y", "knee", "ankle_y", "ankle_x",
             "abdomen_x", "abdomen_y", "abdomen_z")


def scaled(mult, joints=LEG_TRUNK):
    s = SRC
    for j in joints:
        for full in ((j,) if j.startswith("abdomen") else (j + "_l", j + "_r")):
            def bump(mo, k=mult):
                return mo.group(1) + ("%g" % (float(mo.group(2)) * k)) + mo.group(3)
            s = re.sub(r'(<position name="' + full + r'"[^>]*?kp=")([\d.]+)(")', bump, s)
            s = re.sub(r'(<position name="' + full + r'"[^>]*?kv=")([\d.]+)(")', bump, s)
    return s


def support_frac(m, d):
    lo, hi = [], []
    for s in "lr":
        g = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_" + s + "_geom")
        c, R, half = d.geom_xpos[g], d.geom_xmat[g].reshape(3, 3), m.geom_size[g]
        cor = np.array([c + R @ (np.array([sx, sy, sz]) * half)
                        for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)])
        lo.append(cor[:, 0].min())
        hi.append(cor[:, 0].max())
    return (d.subtree_com[0][0] - min(lo)) / (max(hi) - min(lo)) * 100


def upright_of(m, d, pelvis):
    w, x, y, z = d.xquat[pelvis]
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                  [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                  [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])
    return -(R.T @ np.array([0.0, 0.0, -1.0]))[2]


def hold(xml_text, seconds=10.0):
    path = os.path.join(tempfile.gettempdir(), "athlete_gain_sweep.xml")
    open(path, "w", encoding="utf-8").write(xml_text)
    m = mujoco.MjModel.from_xml_path(path)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    d.ctrl[:] = d.qpos[7:]
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
    stand = float(d.qpos[2])
    upright_s = 0.0
    while d.time < seconds:
        mujoco.mj_step(m, d)
        if d.xpos[pelvis][2] > stand * 0.6 and upright_of(m, d, pelvis) > 0.4:
            upright_s = d.time
        else:
            break
    mujoco.mj_forward(m, d)
    return upright_s, support_frac(m, d), float(d.xpos[pelvis][2])


if __name__ == "__main__":
    print("Holding the keyframe pose with zero action for 10 s. A body that cannot do this is")
    print("falling over before the policy has done anything, in every episode of every run.")
    print("")
    print("%6s %9s %10s %9s" % ("kp x", "upright", "CoM supp%", "pelvis_z"))
    for mult in (1, 2, 3, 4, 6, 8, 12, 20):
        up, frac, pz = hold(scaled(mult))
        print("%6d %8.2fs %9.1f%% %8.3f" % (mult, up, frac, pz))
