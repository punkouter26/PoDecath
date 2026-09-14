"""Does a candidate stand pose survive the noise the trainer actually resets with?

`pose_sweep.py` finds poses that hold for ten seconds from one exact initial condition. That is not
the question. `RunToTargetEnv._reset_states` jitters every joint by +-0.05 rad and gives the base
+-0.2 m/s, and domain randomisation moves kp by -20/+25% -- and the lean under test is itself only
0.05 rad, so a fix that works from one seed and nowhere else would look identical in the sweep and
be worthless in training.

This runs each candidate from many noisy resets and reports the fraction still upright at 5 s. A
candidate worth training on has to clear that, not a single lucky rollout.
"""
import os
import sys
import tempfile

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gain_sweep import SRC, scaled, support_frac, upright_of  # noqa: E402

JOINT_JITTER = 0.05      # matches _reset_states
BASE_VEL = 0.2           # matches _reset_states


def build(mult):
    xml = SRC if mult == 1 else scaled(mult)
    path = os.path.join(tempfile.gettempdir(), "athlete_stand_robust_%g.xml" % mult)
    open(path, "w", encoding="utf-8").write(xml)
    return mujoco.MjModel.from_xml_path(path)


def trial(m, rng, ankle_off, hip_off, kp_scale, seconds=5.0):
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    adr = {}
    for name in ("ankle_y_l", "ankle_y_r", "hip_y_l", "hip_y_r"):
        adr[name] = m.jnt_qposadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, name)]
    for name in ("ankle_y_l", "ankle_y_r"):
        d.qpos[adr[name]] += ankle_off
    for name in ("hip_y_l", "hip_y_r"):
        d.qpos[adr[name]] += hip_off
    mujoco.mj_forward(m, d)
    lowest = min(d.geom_xpos[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_" + s + "_geom")][2]
                 - m.geom_size[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_" + s + "_geom")][2]
                 for s in "lr")
    d.qpos[2] += 0.002 - lowest
    target = d.qpos[7:].copy()

    # the trainer's own reset noise, applied on top of the candidate pose
    d.qpos[7:] += rng.uniform(-JOINT_JITTER, JOINT_JITTER, size=m.nq - 7)
    d.qvel[:2] = rng.uniform(-BASE_VEL, BASE_VEL, size=2)
    # domain randomisation moves the gains; the policy never trains on the nominal ones alone
    m.actuator_gainprm[:, 0] *= kp_scale
    m.actuator_biasprm[:, 1] *= kp_scale

    mujoco.mj_forward(m, d)
    d.ctrl[:] = target
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
    stand = 0.902
    while d.time < seconds:
        mujoco.mj_step(m, d)
        if not (d.xpos[pelvis][2] > stand * 0.6 and upright_of(m, d, pelvis) > 0.4):
            return False, d.time, float("nan")
    mujoco.mj_forward(m, d)
    return True, d.time, support_frac(m, d)


if __name__ == "__main__":
    n = 40
    print("%d noisy resets per candidate (joint +-0.05 rad, base +-0.2 m/s, kp x[0.8, 1.25])." % n)
    print("Upright at 5 s is the bar; the shipped model falls at 1.75 s from a clean start.")
    print("")
    print("%5s %8s %8s %10s %10s %10s" % ("kp x", "ankle", "hip", "held 5 s", "mean fall", "CoM supp%"))
    for mult in (1, 2, 3, 4):
        m_nom = build(mult)
        for ankle_off, hip_off in ((0.00, 0.00), (-0.05, 0.05), (-0.08, 0.08), (-0.10, 0.10)):
            rng = np.random.default_rng(0)
            held, fell_at, fracs = 0, [], []
            for _ in range(n):
                m = build(mult)
                ok, t, frac = trial(m, rng, ankle_off, hip_off, rng.uniform(0.8, 1.25))
                if ok:
                    held += 1
                    fracs.append(frac)
                else:
                    fell_at.append(t)
            mf = ("%.2fs" % np.mean(fell_at)) if fell_at else "   -  "
            cf = ("%.1f%%" % np.mean(fracs)) if fracs else "  -  "
            print("%5d %8.2f %8.2f %9.0f%% %10s %10s" % (mult, ankle_off, hip_off, 100 * held / n, mf, cf))
        print("")
