"""
Does the athlete collide with itself where it should, and only there?

    .venv/Scripts/python.exe tools/self_collision_check.py [models/athlete.xml]

The house rule (AGENTS.md, Collision) is that every pair of body parts collides except parent-child
pairs and pairs that overlap in the default stance, and that before training nothing may touch in the
rest pose, the default stance or a normal stride. A pair that touches in one of those is a pair the
policy is pushed apart by from the first step, which reads in training as a body that cannot stand.

Prints the touching pairs and the smallest gap for each of the three, then how often random poses
around the stance put parts in contact, which they should: crossed legs are meant to collide.
Exit code 1 when anything touches in the three poses that must be clear.
"""
import collections
import itertools
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
XML = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "models", "athlete.xml")

m = mujoco.MjModel.from_xml_path(XML)
d = mujoco.MjData(m)
floor = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "floor")
hinges = [j for j in range(m.njnt) if m.jnt_type[j] == mujoco.mjtJoint.mjJNT_HINGE]


def gname(g):
    return (mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) or f"geom{g}").replace("_geom", "")


def jname(j):
    return mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_JOINT, j) or ""


def pose(q):
    d.qpos[:] = q
    d.qpos[2] = 3.0   # clear of the floor: only the body's own parts can touch
    mujoco.mj_forward(m, d)


def touching(q):
    pose(q)
    return {tuple(sorted((gname(c.geom1), gname(c.geom2)))) for c in d.contact[:d.ncon]
            if floor not in (c.geom1, c.geom2)}


excluded = {(int(s) >> 16, int(s) & 0xFFFF) for s in m.exclude_signature}


def smallest_gap(q):
    """Smallest distance between two parts that are allowed to collide, and which two."""
    pose(q)
    best = (1e9, None)
    for a, b in itertools.combinations(range(m.ngeom), 2):
        if floor in (a, b):
            continue
        ba, bb = int(m.geom_bodyid[a]), int(m.geom_bodyid[b])
        if ba == bb or (ba, bb) in excluded or (bb, ba) in excluded:
            continue
        dist = mujoco.mj_geomDistance(m, d, a, b, 1.0, None)
        if dist < best[0]:
            best = (dist, (gname(a), gname(b)))
    return best


def set_joint(q, j, degrees):
    lo, hi = m.jnt_range[j]
    q[m.jnt_qposadr[j]] = min(hi, max(lo, math.radians(degrees)))


rest = m.qpos0.copy()
stance = m.key_qpos[0].copy() if m.nkey else rest
failed = False

print(f"{os.path.basename(XML)}: {m.ngeom - 1} shapes on {m.nbody - 1} bodies, {m.nexclude} excluded pairs")
for label, q in (("rest pose", rest), ("default stance", stance)):
    pairs, gap = touching(q), smallest_gap(q)
    failed |= bool(pairs)
    print(f"  {label}: {'TOUCHING ' + str(sorted(pairs)) if pairs else 'nothing touches'}; "
          f"smallest gap {gap[0] * 1000:.0f} mm ({gap[1][0]} to {gap[1][1]})")

# A stride: the sagittal hip joints swung in opposition with the knees bending, every other hinge that
# swings a limb (shoulders, elbows) taken through the same range, at two amplitudes.
pairs, worst = set(), (1e9, None)
for phase in np.linspace(0.0, 2.0 * math.pi, 49):
    s = math.sin(phase)
    for amp in (25.0, 45.0):
        q = stance.copy()
        for j in hinges:
            n, side = jname(j), (1.0 if jname(j).endswith("_l") else -1.0)
            base = math.degrees(stance[m.jnt_qposadr[j]])
            if n.startswith("hip_y"):
                set_joint(q, j, base - side * amp * s)
            elif n.startswith("knee"):
                set_joint(q, j, base + 40.0 + 40.0 * side * s)
            elif n.startswith("shoulder") or n.startswith("elbow"):
                set_joint(q, j, base + side * amp * s)
        pairs |= touching(q)
        gap = smallest_gap(q)
        if gap[0] < worst[0]:
            worst = gap
failed |= bool(pairs)
print(f"  stride sweep: {'TOUCHING ' + str(sorted(pairs)) if pairs else 'nothing touches'}; "
      f"smallest gap {worst[0] * 1000:.0f} mm ({worst[1][0]} to {worst[1][1]})")

rng = np.random.default_rng(0)
for spread in (15.0, 30.0):
    seen, hit, n = collections.Counter(), 0, 2000
    for _ in range(n):
        q = stance.copy()
        for j in hinges:
            set_joint(q, j, math.degrees(stance[m.jnt_qposadr[j]]) + rng.uniform(-spread, spread))
        now = touching(q)
        hit += bool(now)
        seen.update(now)
    common = ", ".join(f"{a}-{b} {100 * c / n:.0f}%" for (a, b), c in seen.most_common(4))
    print(f"  random poses within {spread:.0f} deg of the stance: {100 * hit / n:.0f}% have parts in contact ({common})")

sys.exit(1 if failed else 0)
