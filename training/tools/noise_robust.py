"""Does the stiffer body survive the policy's own exploration noise, or only a silent policy?

`stand_robust.py` holds the stand pose with zero action. Training never does that: PPO samples
actions from a Gaussian whose standard deviation starts at 0.8 and is still 0.3-0.45 at the end of
every run in this project, and each sample is multiplied by `action_scale` and added to the default
joint target. So the joint the actuator is chasing jitters by roughly `std * action_scale` radians at
50 Hz, and the torque that produces is proportional to kp.

That is the catch in tripling kp: it triples the restoring torque that holds the pose *and* triples
the torque the exploration noise injects. This measures both models against the same noise, and
against an action_scale reduced to compensate, so the trade can be read instead of guessed.

Usage: noise_robust.py <old-model.xml> <new-model.xml>
"""
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gain_sweep import upright_of  # noqa: E402

CONTROL_DT = 0.02       # 50 Hz, decimation 4 on a 0.005 s step
ACTION_CLIP = 3.0


def run(model_path, std, action_scale, n=40, seconds=5.0, seed=0):
    m = mujoco.MjModel.from_xml_path(model_path)
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
    rng = np.random.default_rng(seed)
    held, fell = 0, []
    for _ in range(n):
        d = mujoco.MjData(m)
        mujoco.mj_resetDataKeyframe(m, d, 0)
        default = d.qpos[7:].copy()
        stand = float(d.qpos[2])
        d.qpos[7:] += rng.uniform(-0.05, 0.05, size=m.nq - 7)
        d.qvel[:2] = rng.uniform(-0.2, 0.2, size=2)
        mujoco.mj_forward(m, d)
        lo = m.actuator_ctrlrange[:, 0]
        hi = m.actuator_ctrlrange[:, 1]
        alive = True
        while d.time < seconds and alive:
            # one control step: sample an action the way PPO does, hold it for the decimation window
            a = np.clip(rng.normal(0.0, std, size=m.nu), -ACTION_CLIP, ACTION_CLIP)
            d.ctrl[:] = np.clip(default + a * action_scale, lo, hi)
            t_end = d.time + CONTROL_DT
            while d.time < t_end:
                mujoco.mj_step(m, d)
            if not (d.xpos[pelvis][2] > stand * 0.6 and upright_of(m, d, pelvis) > 0.4):
                alive = False
        if alive:
            held += 1
        else:
            fell.append(d.time)
    return 100.0 * held / n, (np.mean(fell) if fell else float("nan"))


if __name__ == "__main__":
    old, new = sys.argv[1], sys.argv[2]
    print("Upright at 5 s under Gaussian action noise, 40 trials each.")
    print("std 0.0 is the zero-action case; 0.30-0.45 is where every run in this project ends up.")
    print("")
    print("%-26s %8s %8s %10s %10s" % ("model", "act std", "act scale", "held 5 s", "mean fall"))
    for label, path, scale in (("old (kp x1)", old, 0.5),
                               ("new (kp x3)", new, 0.5),
                               ("new (kp x3, scale 0.25)", new, 0.25),
                               ("new (kp x3, scale 0.167)", new, 0.167)):
        for std in (0.0, 0.15, 0.30, 0.45):
            pct, mf = run(path, std, scale)
            mfs = ("%.2fs" % mf) if mf == mf else "   -  "
            print("%-26s %8.2f %8.3f %9.0f%% %10s" % (label, std, scale, pct, mfs))
        print("")
