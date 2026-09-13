"""Generate a MuJoCo MJCF humanoid from a Blender armature description (rigs/*.json).

Frames
------
Rig JSON is in Blender's Z-up frame with the character facing -Y and +X = character left.
MJCF uses X forward, Y left, Z up (right-handed):   mj = (-b.y, b.x, b.z)

Joint layout (21 DoF, all hinges, PD position actuators)
--------------------------------------------------------
  abdomen_z, abdomen_y, abdomen_x            pelvis -> torso
  hip_x, hip_z, hip_y, knee, ankle_y, ankle_x  per leg (l, r)
  shoulder_x, shoulder_z, elbow              per arm (l, r)

The order of <joint> elements in the file is the policy joint order (see policy_config.json
written next to the model). Unity's MjcfImporter reads the same file, so the two runtimes share
one source of truth for geometry, limits and gains.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from typing import Dict, List

import numpy as np

# ---- tunables ---------------------------------------------------------------------------------
GAINS = {  # kp [N m / rad], kv [N m s / rad], force limit [N m]
    "abdomen": (100.0, 5.0, 100.0),
    "hip": (120.0, 6.0, 140.0),
    "knee": (120.0, 6.0, 140.0),
    "ankle": (50.0, 2.5, 60.0),
    "shoulder": (30.0, 1.5, 40.0),
    "elbow": (25.0, 1.2, 30.0),
}
JOINT_GAINS = {  # per-joint override of the group entry, applied after `rig["gains"]`
    # The ankle group covers both axes, but only plantarflexion needs sprint-grade torque: the human
    # peak about the pitch axis is 200-280 N m, against 40-60 N m for inversion/eversion. Giving both
    # 220 would let the policy roll the foot over with force no ankle can produce.
    "ankle_x": (80.0, 4.0, 60.0),
}
RANGES_DEG = {  # symmetric or (lo, hi); side-dependent signs applied for arms
    "abdomen_z": (-45, 45), "abdomen_y": (-60, 30), "abdomen_x": (-35, 35),
    # hip_x stays symmetric on purpose. Human abduction (~45 deg) and adduction (~25 deg) are not
    # symmetric, but RANGES_DEG is not mirrored for the legs -- an asymmetric range here would be
    # correct on one side and backwards on the other. Adduction past the real limit is instead stopped
    # by the thighs actually touching, now that self-collision is on.
    "hip_x": (-40, 40), "hip_z": (-35, 35), "hip_y": (-120, 30),
    "knee": (0, 150),
    # Negative = dorsiflexion (toes up), positive = plantarflexion (toes down): rotating the foot's
    # +x toe direction about +y by a negative angle lifts it. The human ankle is not symmetric --
    # ~20 deg up against ~50 deg down -- and the old +-45 gave it more than twice the dorsiflexion a
    # real ankle has.
    "ankle_y": (-20, 50), "ankle_x": (-25, 25),
    "shoulder_x": (-170, 30),  # left arm: negative = arm down; mirrored on the right
    "shoulder_z": (-90, 90),
    "elbow": (-150, 0),        # left; mirrored on the right
}
ARMATURE = {  # reflected rotational inertia at the joint [kg m^2]
    # Every joint used to carry 0.01, which is far below the limb inertia any of these actually swing
    # and is the classic recipe for a stiff, buzzing PD loop that only holds together because the
    # timestep is small. These are order-of-magnitude estimates of the segment inertia each joint
    # drives; they cost nothing dynamically (a hip sees ~1.9 kg m^2 of leg) and condition the solver.
    "abdomen": 0.08, "hip": 0.10, "knee": 0.10, "ankle": 0.03,
    "shoulder": 0.03, "elbow": 0.02,
}
# Winter / Dempster segment masses as a fraction of total body mass. `rig["mass_kg"]` is the owner's
# measured figure for the athlete; before this table existed the generator ignored it entirely and let
# every geom default to density 1000, which produced a 49.8 kg body for a 1.74 m athlete -- a BMI of
# 16.5. Masses are written per geom, so MuJoCo still derives each inertia tensor from the real shape.
SEGMENT_MASS_FRAC = {
    "pelvis": 0.142,      # split across pelvis_geom / pelvis_up by volume
    "torso": 0.355,       # thorax + abdomen, head excluded
    "head": 0.081,
    "upper_arm": 0.028,
    "forearm": 0.016,
    "hand": 0.006,
    "thigh": 0.100,
    "shin": 0.0465,
    "foot": 0.0145,
}
# Peak joint speeds a human sprinter actually reaches [rad/s]; house rule 15. Enforced as a reward
# penalty in the envs rather than a hard clamp, and exported in the policy config so Unity can agree.
VELOCITY_LIMIT = {
    "abdomen": 8.0, "hip": 15.0, "knee": 20.0, "ankle": 14.0,
    "shoulder": 15.0, "elbow": 18.0,
}
STAND_DEG = {  # default posture (external convention) used as policy default_joint_pos
    "abdomen_z": 0, "abdomen_y": 0, "abdomen_x": 0,
    "hip_x": 0, "hip_z": 0, "hip_y": 0, "knee": 0, "ankle_y": 0, "ankle_x": 0,
    "shoulder_x": -78, "shoulder_z": 0, "elbow": -35,
}
RADII = {"pelvis": 0.085, "torso": 0.085, "head": 0.085, "thigh": 0.06, "shin": 0.045,
         "upper_arm": 0.035, "forearm": 0.03, "hand": 0.03}


def b2m(v) -> np.ndarray:
    """Blender (x left, -y forward, z up) -> MJCF (x forward, y left, z up)."""
    return np.array([-v[1], v[0], v[2]], dtype=float)


def fmt(v) -> str:
    return " ".join(f"{float(x):.4f}" for x in v)


class Builder:
    def __init__(self, rig: Dict):
        self.rig = rig
        self.bones = {b["name"]: b for b in rig["bones"]}
        self.map = rig["map"]
        self.gains = {k: tuple(v) for k, v in rig.get("gains", GAINS).items()}
        for k, v in GAINS.items():
            self.gains.setdefault(k, v)
        self.joint_gains = {k: tuple(v) for k, v in rig.get("joint_gains", JOINT_GAINS).items()}
        for k, v in JOINT_GAINS.items():
            self.joint_gains.setdefault(k, v)
        self.armature = dict(ARMATURE, **rig.get("armature_kgm2", {}))
        self.vel_limit = dict(VELOCITY_LIMIT, **rig.get("velocity_limit", {}))
        self.mass_kg = float(rig.get("mass_kg", 0.0) or 0.0)
        self.lines: List[str] = []
        self.joints: List[Dict] = []   # policy order
        self.actuators: List[str] = []

    def seg_mass(self, segment: str, share: float = 1.0) -> float:
        """Mass for one geom, as its share of the segment's Winter fraction of `rig["mass_kg"]`.

        Returns 0.0 when the rig declares no mass, which leaves the geom on the density default and
        keeps this generator working for rigs that predate the table.
        """
        if self.mass_kg <= 0.0:
            return 0.0
        return self.mass_kg * SEGMENT_MASS_FRAC[segment] * share

    def mass_attr(self, segment: str, share: float = 1.0) -> str:
        m = self.seg_mass(segment, share)
        return f'mass="{m:.4f}" ' if m > 0.0 else ""

    def has(self, key: str) -> bool:
        return key in self.map and self.map[key] in self.bones

    def head(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["head"])

    def tail(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["tail"])

    # ---- element helpers ---------------------------------------------------------------------
    @staticmethod
    def capsule_volume(a: np.ndarray, b: np.ndarray, radius: float) -> float:
        return float(math.pi * radius ** 2 * np.linalg.norm(b - a) + (4.0 / 3.0) * math.pi * radius ** 3)

    def capsule(self, name: str, a_local: np.ndarray, b_local: np.ndarray, radius: float, extra: str = "",
                segment: str = "", share: float = 1.0) -> str:
        mass = self.mass_attr(segment, share) if segment else ""
        return (f'<geom name="{name}" type="capsule" fromto="{fmt(a_local)} {fmt(b_local)}" '
                f'size="{radius:.4f}" {mass}{extra}/>')

    def joint(self, name: str, axis: str, group: str, side: str = "") -> str:
        lo, hi = RANGES_DEG[name]
        default = STAND_DEG[name]
        sign = 1.0
        if side == "r" and name in ("shoulder_x", "elbow"):
            # mirror the arm joints for the right side
            lo, hi = -hi, -lo
            default = -default
        full = f"{name}_{side}" if side else name
        kp, kv, fl = self.joint_gains.get(name, self.gains[group])
        arm = self.armature[group]
        self.joints.append({
            "name": full, "group": group, "axis": axis,
            "lower": math.radians(lo), "upper": math.radians(hi), "default": math.radians(default),
            "kp": kp, "kv": kv, "force_limit": fl, "armature": arm,
            "velocity_limit": self.vel_limit[group],
        })
        self.actuators.append(
            f'<position name="{full}" joint="{full}" kp="{kp}" kv="{kv}" forcerange="-{fl} {fl}" '
            f'ctrlrange="{math.radians(lo):.4f} {math.radians(hi):.4f}"/>')
        return (f'<joint name="{full}" type="hinge" axis="{axis}" range="{lo} {hi}" '
                f'damping="0.5" armature="{arm}" stiffness="0"/>')

    # ---- build ---------------------------------------------------------------------------------
    def build(self) -> str:
        L = self.lines
        pelvis = self.head("pelvis")
        spine = self.head("spine") if self.has("spine") else self.tail("pelvis")
        neck = self.head("neck") if self.has("neck") else self.tail("torso")
        head_c = 0.5 * (self.head("head") + self.tail("head"))

        L.append(f'<body name="pelvis" pos="{fmt(pelvis)}">')
        L.append('  <freejoint name="root"/>')
        L.append('  <site name="imu" pos="0 0 0" size="0.01"/>')
        # The pelvis is two capsules; split the segment's mass between them by volume so the centre of
        # mass lands where the shape actually is.
        p_a, p_b = np.array([0, -0.07, 0.0]), np.array([0, 0.07, 0.0])
        p_up_b = spine - pelvis - np.array([0, 0, 0.02])
        v_lo = self.capsule_volume(p_a, p_b, RADII["pelvis"])
        v_up = self.capsule_volume(np.zeros(3), p_up_b, RADII["pelvis"] * 0.9)
        f_lo = v_lo / (v_lo + v_up)
        L.append('  ' + self.capsule("pelvis_geom", p_a, p_b, RADII["pelvis"], segment="pelvis", share=f_lo))
        L.append('  ' + self.capsule("pelvis_up", np.zeros(3), p_up_b, RADII["pelvis"] * 0.9,
                                     segment="pelvis", share=1.0 - f_lo))

        # torso
        L.append(f'  <body name="torso" pos="{fmt(spine - pelvis)}">')
        L.append('    ' + self.joint("abdomen_z", "0 0 1", "abdomen"))
        L.append('    ' + self.joint("abdomen_y", "0 1 0", "abdomen"))
        L.append('    ' + self.joint("abdomen_x", "1 0 0", "abdomen"))
        L.append('    ' + self.capsule("torso_geom", np.array([0, 0, 0.02]), neck - spine - np.array([0, 0, 0.01]),
                                       RADII["torso"], segment="torso"))
        L.append(f'    <geom name="head_geom" type="sphere" pos="{fmt(head_c - spine)}" '
                 f'size="{RADII["head"]:.4f}" {self.mass_attr("head")}/>')
        for side, key_u, key_f, key_h in (("l", "upper_arm_l", "forearm_l", "hand_l"), ("r", "upper_arm_r", "forearm_r", "hand_r")):
            sh = self.head(key_u); el = self.head(key_f); wr = self.head(key_h)
            tip = self.tail(f"hand_tip_{side}") if self.has(f"hand_tip_{side}") else self.tail(key_h)
            L.append(f'    <body name="upper_arm_{side}" pos="{fmt(sh - spine)}">')
            L.append('      ' + self.joint("shoulder_x", "1 0 0", "shoulder", side))
            L.append('      ' + self.joint("shoulder_z", "0 0 1", "shoulder", side))
            L.append('      ' + self.capsule(f"upper_arm_{side}_geom", np.zeros(3), el - sh, RADII["upper_arm"],
                                             segment="upper_arm"))
            L.append(f'      <body name="forearm_{side}" pos="{fmt(el - sh)}">')
            L.append('        ' + self.joint("elbow", "0 0 1", "elbow", side))
            L.append('        ' + self.capsule(f"forearm_{side}_geom", np.zeros(3), wr - el, RADII["forearm"],
                                               segment="forearm"))
            L.append('        ' + self.capsule(f"hand_{side}_geom", wr - el, tip - el, RADII["hand"],
                                               segment="hand"))
            L.append('      </body>')
            L.append('    </body>')
        L.append('  </body>')  # torso

        # legs
        for side, key_t, key_s, key_f in (("l", "thigh_l", "shin_l", "foot_l"), ("r", "thigh_r", "shin_r", "foot_r")):
            hip = self.head(key_t); knee = self.head(key_s); ankle = self.head(key_f)
            toe = self.tail(f"toe_{side}") if self.has(f"toe_{side}") else self.tail(key_f)
            L.append(f'  <body name="thigh_{side}" pos="{fmt(hip - pelvis)}">')
            L.append('    ' + self.joint("hip_x", "1 0 0", "hip", side))
            L.append('    ' + self.joint("hip_z", "0 0 1", "hip", side))
            L.append('    ' + self.joint("hip_y", "0 1 0", "hip", side))
            L.append('    ' + self.capsule(f"thigh_{side}_geom", np.zeros(3), knee - hip, RADII["thigh"],
                                           segment="thigh"))
            L.append(f'    <body name="shin_{side}" pos="{fmt(knee - hip)}">')
            L.append('      ' + self.joint("knee", "0 1 0", "knee", side))
            L.append('      ' + self.capsule(f"shin_{side}_geom", np.zeros(3), ankle - knee, RADII["shin"],
                                             segment="shin"))
            L.append(f'      <body name="foot_{side}" pos="{fmt(ankle - knee)}">')
            L.append('        ' + self.joint("ankle_y", "0 1 0", "ankle", side))
            L.append('        ' + self.joint("ankle_x", "1 0 0", "ankle", side))
            foot_len = max(0.12, float(toe[0] - ankle[0]) + 0.03)
            half = np.array([foot_len * 0.5, 0.045, 0.028])
            center = np.array([foot_len * 0.5 - 0.04, 0.0, -float(ankle[2]) + half[2]])
            L.append(f'        <geom name="foot_{side}_geom" type="box" pos="{fmt(center)}" size="{fmt(half)}" '
                     f'{self.mass_attr("foot")}friction="1.0 0.005 0.0001"/>')
            L.append(f'        <site name="foot_{side}_site" pos="{fmt(center)}" size="0.01"/>')
            L.append('      </body>')
            L.append('    </body>')
            L.append('  </body>')
        L.append('</body>')  # pelvis

        body = "\n    ".join(L)
        act = "\n    ".join(self.actuators)
        # Sole height in the stand pose works out to exactly (keyframe z - pelvis z): the chain from
        # pelvis to foot body sums to (ankle - pelvis).z and the foot box centre cancels the rest. The
        # old +0.01 therefore floated the athlete a centimetre off the floor, and `_reset_states` added
        # another 0.02 on top, so every episode opened with a 3 cm drop and an impact transient before
        # the policy had done anything. 2 mm is contact margin, not a fall.
        keyframe_q = ["0", "0", f"{pelvis[2] + 0.002:.4f}", "1", "0", "0", "0"] + [f"{j['default']:.4f}" for j in self.joints]
        exclude = "\n    ".join(
            f'<exclude body1="{a}" body2="{b}"/>' for a, b in self.contact_exclusions())
        xml = f"""<mujoco model="{self.rig.get('name', 'athlete')}">
  <compiler angle="degree" inertiafromgeom="true" autolimits="true"/>
  <option timestep="0.005" iterations="10" ls_iterations="10" integrator="implicitfast">
    <flag eulerdamp="disable"/>
  </option>
  <default>
    <geom contype="1" conaffinity="1" condim="3" friction="1.0 0.005 0.0001" density="1000" margin="0"/>
    <joint limited="true"/>
    <position ctrllimited="true"/>
  </default>
  <asset>
    <texture name="grid" type="2d" builtin="checker" rgb1=".2 .3 .4" rgb2=".1 .15 .2" width="300" height="300"/>
    <material name="grid" texture="grid" texrepeat="8 8" reflectance="0"/>
  </asset>
  <worldbody>
    <light pos="0 0 3" dir="0 0 -1" directional="true"/>
    <geom name="floor" type="plane" size="0 0 0.05" material="grid" contype="1" conaffinity="1" friction="1.0 0.005 0.0001"/>
    {body}
  </worldbody>
  <contact>
    {exclude}
  </contact>
  <actuator>
    {act}
  </actuator>
  <keyframe>
    <key name="stand" qpos="{' '.join(keyframe_q)}"/>
  </keyframe>
</mujoco>
"""
        return xml

    def contact_exclusions(self):
        """Body pairs that must not collide once self-collision is on.

        Self-collision used to be off entirely -- every geom carried `conaffinity="0"`, so only the
        floor could touch anything. Measured on the shipped rig: both hips driven fully inward until
        the thigh capsules overlapped by 12 cm still reported `ncon = 0`. The legs were ghosts to each
        other, and every gait ever trained was free to scissor them.

        The reason it was switched off is on record in AGENTS.md -- the pelvis and thigh capsules
        overlap by construction at the hip and jammed the joint at its abduction limit. That is an
        argument for excluding the pairs that are adjacent across a joint, not for excluding all of
        them. These are those pairs: everything else now collides.
        """
        pairs = [("pelvis", "torso")]
        for side in ("l", "r"):
            pairs += [
                ("pelvis", f"thigh_{side}"),
                (f"thigh_{side}", f"shin_{side}"),
                (f"shin_{side}", f"foot_{side}"),
                ("torso", f"upper_arm_{side}"),
                (f"upper_arm_{side}", f"forearm_{side}"),
            ]
        return pairs

    def policy_config(self, model_name: str) -> Dict:
        return {
            "model": model_name,
            "joint_order": [j["name"] for j in self.joints],
            "default_joint_pos": [j["default"] for j in self.joints],
            "lower": [j["lower"] for j in self.joints],
            "upper": [j["upper"] for j in self.joints],
            "kp": [j["kp"] for j in self.joints],
            "kv": [j["kv"] for j in self.joints],
            "force_limit": [j["force_limit"] for j in self.joints],
            "armature": [j["armature"] for j in self.joints],
            "velocity_limit": [j["velocity_limit"] for j in self.joints],
            "total_mass_kg": self.mass_kg,
            "action_scale": 0.5,
            # Must match the trainer's own clamp (`RunToTargetEnv.action_clip`). Unity hard-coded 5
            # to match the old trainer value; carrying it here stops the two drifting apart again.
            "action_clip": 3.0,
            "spawn_clearance_m": 0.002,
            "physics_hz": 200,
            "control_decimation": 4,
            "observation": ["base_lin_vel", "base_ang_vel", "projected_gravity", "target_command",
                            "joint_pos_rel", "joint_vel", "last_action", "foot_contact", "base_height"],
            "target_command": "unit direction (x, y) to target in the base yaw frame, plus min(distance, 10) / 10",
            "foot_contact": "one flag per foot, 1 when the sole is within sole_contact_height of the support plane",
            "base_height": "pelvis height above the support plane, metres",
            "sole_contact_height": 0.03,
        }


def chain_multi_joint_bodies(xml: str) -> str:
    """Split every body with several hinge joints into a chain of one-hinge bodies (tiny massless links).

    MuJoCo semantics are unchanged (sequential hinges), but importers that map a multi-joint body onto a
    single D6 joint (Isaac Sim's MJCF importer) get exact per-axis joints instead of PhysX's fixed X/Y/Z order.
    """
    import xml.etree.ElementTree as ET

    root = ET.fromstring(xml)
    compiler = root.find("compiler")
    if compiler is not None:
        compiler.set("inertiafromgeom", "auto")   # explicit <inertial> on the dummy links, geoms elsewhere

    def split(body: ET.Element) -> None:
        for child in list(body.findall("body")):
            split(child)
        joints = [j for j in body.findall("joint") if j.get("type", "hinge") == "hinge"]
        if len(joints) <= 1:
            return
        name = body.get("name", "body")
        # keep the last joint on the real body; earlier joints get their own links, nested in order
        for j in joints[:-1]:
            body.remove(j)
        # rebuild: outer link holds joint[0], nested link holds joint[1], ..., real body holds joint[-1]
        outer = None
        parent_holder = None
        for k, j in enumerate(joints[:-1]):
            link = ET.Element("body", {"name": f"{name}__link{k}", "pos": body.get("pos", "0 0 0") if k == 0 else "0 0 0"})
            if body.get("quat") and k == 0:
                link.set("quat", body.get("quat"))
            inertial = ET.SubElement(link, "inertial", {"pos": "0 0 0", "mass": "0.02", "diaginertia": "1e-4 1e-4 1e-4"})
            link.append(j)
            if outer is None:
                outer = link
            else:
                parent_holder.append(link)
            parent_holder = link
        body.set("pos", "0 0 0")
        if body.get("quat"):
            del body.attrib["quat"]
        parent_holder.append(body)
        # swap the original body for the outer link in its parent
        for parent in root.iter():
            for idx, ch in enumerate(list(parent)):
                if ch is body:
                    parent.remove(body)
                    parent.insert(idx, outer)
                    return

    world = root.find("worldbody")
    for b in list(world.findall("body")):
        split(b)
    ET.indent(root, space="  ")
    return ET.tostring(root, encoding="unicode")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--rig", default=os.path.join(os.path.dirname(__file__), "rigs", "matt.json"))
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "models", "athlete.xml"))
    ap.add_argument("--name", default="athlete")
    ap.add_argument("--chain", action="store_true", help="also write <out>_chain.xml with one hinge per body (for Isaac Sim)")
    args = ap.parse_args()

    with open(args.rig, "r", encoding="utf-8") as f:
        rig = json.load(f)
    rig["name"] = args.name
    b = Builder(rig)
    xml = b.build()
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(xml)
    if args.chain:
        chain_path = os.path.splitext(args.out)[0] + "_chain.xml"
        with open(chain_path, "w", encoding="utf-8") as f:
            f.write(chain_multi_joint_bodies(xml))
        print(f"wrote {chain_path}")
    cfg_path = os.path.splitext(args.out)[0] + "_policy_config.json"
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump(b.policy_config(os.path.basename(args.out)), f, indent=2)
    print(f"wrote {args.out} ({len(b.joints)} joints) and {cfg_path}")


if __name__ == "__main__":
    main()
