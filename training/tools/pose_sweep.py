"""Is the stand keyframe out of balance, and can a pose fix replace a stiffness fix?

`gain_sweep.py` shows the body needs six times its shipped joint stiffness to hold its own stand
pose. Six times is a blunt instrument: the force limits stay human, so a gain that high just means
the actuator saturates for any error past about 0.15 rad, which is bang-bang control wearing a PD
costume. The other reading of the same measurement is that the pose itself is wrong -- the centre of
mass starts at 17% of the heel-to-toe span, where a standing human sits near 45% -- so the joints are
being asked to hold a lean that should not be there in the first place.

This sweeps a forward lean (ankle dorsiflexion, with the hip taking the opposite sign so the trunk
stays vertical rather than the whole body pitching) against a stiffness multiplier, and reports how
long the body holds and where its mass ends up over the foot.
"""
import os
import sys
import tempfile

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gain_sweep import SRC, scaled, support_frac, upright_of  # noqa: E402


def hold_posed(xml_text, ankle_off, hip_off, knee_off, seconds=10.0):
    """Offset the stand keyframe and hold it. The ctrl target is the offset pose, not the original,
    so this measures a different stand pose rather than a PD fighting the one it was given."""
    path = os.path.join(tempfile.gettempdir(), "athlete_pose_sweep.xml")
    open(path, "w", encoding="utf-8").write(xml_text)
    m = mujoco.MjModel.from_xml_path(path)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)

    idx = {}
    for name in ("ankle_y_l", "ankle_y_r", "hip_y_l", "hip_y_r", "knee_l", "knee_r"):
        j = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, name)
        idx[name] = m.jnt_qposadr[j]
    for name in ("ankle_y_l", "ankle_y_r"):
        d.qpos[idx[name]] += ankle_off
    for name in ("hip_y_l", "hip_y_r"):
        d.qpos[idx[name]] += hip_off
    for name in ("knee_l", "knee_r"):
        d.qpos[idx[name]] += knee_off
    # Re-seat the feet: changing the leg angles lifts or buries the soles.
    mujoco.mj_forward(m, d)
    lowest = min(d.geom_xpos[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_" + s + "_geom")][2]
                 - m.geom_size[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "foot_" + s + "_geom")][2]
                 for s in "lr")
    d.qpos[2] += 0.002 - lowest
    mujoco.mj_forward(m, d)

    d.ctrl[:] = d.qpos[7:]
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")
    stand = float(d.qpos[2])
    start_frac = support_frac(m, d)
    upright_s = 0.0
    while d.time < seconds:
        mujoco.mj_step(m, d)
        if d.xpos[pelvis][2] > stand * 0.6 and upright_of(m, d, pelvis) > 0.4:
            upright_s = d.time
        else:
            break
    mujoco.mj_forward(m, d)
    return upright_s, start_frac, support_frac(m, d), stand


if __name__ == "__main__":
    print("Forward lean at the ankle, trunk kept vertical by the hip. 0% = heel edge, 100% = toe.")
    print("A standing human sits near 45%. The shipped keyframe starts at 17%.")
    print("")
    print("%5s %8s %8s %9s %10s %9s %9s" %
          ("kp x", "ankle", "hip", "upright", "start%", "end%", "stand_h"))
    for mult in (1, 2, 3):
        xml = scaled(mult) if mult != 1 else SRC
        for ankle_off, hip_off in ((0.00, 0.00), (-0.05, 0.05), (-0.10, 0.10),
                                   (-0.15, 0.15), (-0.20, 0.20), (-0.25, 0.25)):
            up, s0, s1, h = hold_posed(xml, ankle_off, hip_off, 0.0)
            print("%5d %8.2f %8.2f %8.2fs %9.1f%% %8.1f%% %8.3f" % (mult, ankle_off, hip_off, up, s0, s1, h))
        print("")
