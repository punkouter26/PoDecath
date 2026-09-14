"""What is the policy actually doing, step by step?

`v_toward` 0.68 with `duty_factor` 0.73 can be a slow walk or a controlled topple, and the summary
numbers `eval_100m.py` prints cannot tell them apart. This dumps one episode at the control rate:
where the pelvis is, how high it is, which feet are down, and what the legs are doing -- so a stride
can be counted, or its absence seen.

    tools/gait_trace.py --ckpt checkpoints/run_to_target/k2_var025/latest.pt --action-scale 0.167
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from athlete_rollout import Rollout  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "athlete.xml"))
    ap.add_argument("--action-scale", type=float, default=None)
    ap.add_argument("--seconds", type=float, default=8.0)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--every", type=int, default=5, help="print one row per N control steps")
    a = ap.parse_args()

    r = Rollout(a.xml, a.ckpt, target_dist=10.0, seed=a.seed, action_scale=a.action_scale)
    r.reset()
    print("checkpoint %s (iter %d), action_scale %.3f" % (os.path.basename(a.ckpt), r.iters, r.action_scale))
    print("")
    print("%6s %7s %7s %7s %7s  %-5s %8s %8s" %
          ("t", "x", "y", "pelv_z", "v", "feet", "hipL", "kneeL"))

    order = r.cfg["joint_order"]
    i_hip, i_knee = order.index("hip_y_l"), order.index("knee_l")
    n = int(a.seconds / (r.decimation * 0.005))
    prev = None
    steps_l = steps_r = 0
    contact_prev = np.array([True, True])
    t = 0.0
    for k in range(n):
        info = r.step()
        alive = not info["fell"]
        t = r.d.time
        pos = r.d.xpos[r.pelvis]
        contact = r.foot_contact()
        for j, s in enumerate(contact):
            if s and not contact_prev[j]:
                if j == 0:
                    steps_l += 1
                else:
                    steps_r += 1
        contact_prev = contact
        v = 0.0 if prev is None else (pos[0] - prev) / (r.decimation * 0.005)
        prev = pos[0]
        if k % a.every == 0:
            feet = ("L" if contact[0] else "-") + ("R" if contact[1] else "-")
            print("%6.2f %7.3f %7.3f %7.3f %7.2f  %-5s %8.3f %8.3f" %
                  (t, pos[0], pos[1], pos[2], v, feet,
                   r.d.qpos[7 + i_hip], r.d.qpos[7 + i_knee]))
        if not alive:
            print("  FELL at t=%.2f s, x=%.2f m" % (t, pos[0]))
            break
    print("")
    print("footfalls: left %d, right %d over %.2f s" % (steps_l, steps_r, t))
    if steps_l + steps_r >= 4 and min(steps_l, steps_r) >= 2:
        print("  -> alternating footfalls: this is stepping")
    else:
        print("  -> too few or one-sided footfalls: this is not a gait")


if __name__ == "__main__":
    main()
