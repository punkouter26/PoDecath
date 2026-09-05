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
RANGES_DEG = {  # symmetric or (lo, hi); side-dependent signs applied for arms
    "abdomen_z": (-45, 45), "abdomen_y": (-60, 30), "abdomen_x": (-35, 35),
    "hip_x": (-40, 40), "hip_z": (-35, 35), "hip_y": (-120, 30),
    "knee": (0, 150), "ankle_y": (-45, 45), "ankle_x": (-25, 25),
    "shoulder_x": (-170, 30),  # left arm: negative = arm down; mirrored on the right
    "shoulder_z": (-90, 90),
    "elbow": (-150, 0),        # left; mirrored on the right
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
        self.lines: List[str] = []
        self.joints: List[Dict] = []   # policy order
        self.actuators: List[str] = []

    def has(self, key: str) -> bool:
        return key in self.map and self.map[key] in self.bones

    def head(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["head"])

    def tail(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["tail"])

    # ---- element helpers ---------------------------------------------------------------------
    def capsule(self, name: str, a_local: np.ndarray, b_local: np.ndarray, radius: float, extra: str = "") -> str:
        return f'<geom name="{name}" type="capsule" fromto="{fmt(a_local)} {fmt(b_local)}" size="{radius:.4f}" {extra}/>'

    def joint(self, name: str, axis: str, group: str, side: str = "") -> str:
        lo, hi = RANGES_DEG[name]
        default = STAND_DEG[name]
        sign = 1.0
        if side == "r" and name in ("shoulder_x", "elbow"):
            # mirror the arm joints for the right side
            lo, hi = -hi, -lo
            default = -default
        full = f"{name}_{side}" if side else name
        self.joints.append({
            "name": full, "group": group, "axis": axis,
            "lower": math.radians(lo), "upper": math.radians(hi), "default": math.radians(default),
        })
        kp, kv, fl = self.gains[group]
        self.actuators.append(
            f'<position name="{full}" joint="{full}" kp="{kp}" kv="{kv}" forcerange="-{fl} {fl}" '
            f'ctrlrange="{math.radians(lo):.4f} {math.radians(hi):.4f}"/>')
        return (f'<joint name="{full}" type="hinge" axis="{axis}" range="{lo} {hi}" '
                f'damping="0.5" armature="0.01" stiffness="0"/>')

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
        L.append('  ' + self.capsule("pelvis_geom", np.array([0, -0.07, 0.0]), np.array([0, 0.07, 0.0]), RADII["pelvis"]))
        L.append('  ' + self.capsule("pelvis_up", np.zeros(3), spine - pelvis - np.array([0, 0, 0.02]), RADII["pelvis"] * 0.9))

        # torso
        L.append(f'  <body name="torso" pos="{fmt(spine - pelvis)}">')
        L.append('    ' + self.joint("abdomen_z", "0 0 1", "abdomen"))
        L.append('    ' + self.joint("abdomen_y", "0 1 0", "abdomen"))
        L.append('    ' + self.joint("abdomen_x", "1 0 0", "abdomen"))
        L.append('    ' + self.capsule("torso_geom", np.array([0, 0, 0.02]), neck - spine - np.array([0, 0, 0.01]), RADII["torso"]))
        L.append(f'    <geom name="head_geom" type="sphere" pos="{fmt(head_c - spine)}" size="{RADII["head"]:.4f}"/>')
        for side, key_u, key_f, key_h in (("l", "upper_arm_l", "forearm_l", "hand_l"), ("r", "upper_arm_r", "forearm_r", "hand_r")):
            sh = self.head(key_u); el = self.head(key_f); wr = self.head(key_h)
            tip = self.tail(f"hand_tip_{side}") if self.has(f"hand_tip_{side}") else self.tail(key_h)
            L.append(f'    <body name="upper_arm_{side}" pos="{fmt(sh - spine)}">')
            L.append('      ' + self.joint("shoulder_x", "1 0 0", "shoulder", side))
            L.append('      ' + self.joint("shoulder_z", "0 0 1", "shoulder", side))
            L.append('      ' + self.capsule(f"upper_arm_{side}_geom", np.zeros(3), el - sh, RADII["upper_arm"]))
            L.append(f'      <body name="forearm_{side}" pos="{fmt(el - sh)}">')
            L.append('        ' + self.joint("elbow", "0 0 1", "elbow", side))
            L.append('        ' + self.capsule(f"forearm_{side}_geom", np.zeros(3), wr - el, RADII["forearm"]))
            L.append('        ' + self.capsule(f"hand_{side}_geom", wr - el, tip - el, RADII["hand"]))
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
            L.append('    ' + self.capsule(f"thigh_{side}_geom", np.zeros(3), knee - hip, RADII["thigh"]))
            L.append(f'    <body name="shin_{side}" pos="{fmt(knee - hip)}">')
            L.append('      ' + self.joint("knee", "0 1 0", "knee", side))
            L.append('      ' + self.capsule(f"shin_{side}_geom", np.zeros(3), ankle - knee, RADII["shin"]))
            L.append(f'      <body name="foot_{side}" pos="{fmt(ankle - knee)}">')
            L.append('        ' + self.joint("ankle_y", "0 1 0", "ankle", side))
            L.append('        ' + self.joint("ankle_x", "1 0 0", "ankle", side))
            foot_len = max(0.12, float(toe[0] - ankle[0]) + 0.03)
            half = np.array([foot_len * 0.5, 0.045, 0.028])
            center = np.array([foot_len * 0.5 - 0.04, 0.0, -float(ankle[2]) + half[2]])
            L.append(f'        <geom name="foot_{side}_geom" type="box" pos="{fmt(center)}" size="{fmt(half)}" friction="1.0 0.005 0.0001"/>')
            L.append(f'        <site name="foot_{side}_site" pos="{fmt(center)}" size="0.01"/>')
            L.append('      </body>')
            L.append('    </body>')
            L.append('  </body>')
        L.append('</body>')  # pelvis

        body = "\n    ".join(L)
        act = "\n    ".join(self.actuators)
        keyframe_q = ["0", "0", f"{pelvis[2] + 0.01:.4f}", "1", "0", "0", "0"] + [f"{j['default']:.4f}" for j in self.joints]
        xml = f"""<mujoco model="{self.rig.get('name', 'athlete')}">
  <compiler angle="degree" inertiafromgeom="true" autolimits="true"/>
  <option timestep="0.005" iterations="6" ls_iterations="8" integrator="implicitfast">
    <flag eulerdamp="disable"/>
  </option>
  <default>
    <geom contype="1" conaffinity="0" condim="3" friction="1.0 0.005 0.0001" density="1000" margin="0.001"/>
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
  <actuator>
    {act}
  </actuator>
  <keyframe>
    <key name="stand" qpos="{' '.join(keyframe_q)}"/>
  </keyframe>
</mujoco>
"""
        return xml

    def policy_config(self, model_name: str) -> Dict:
        return {
            "model": model_name,
            "joint_order": [j["name"] for j in self.joints],
            "default_joint_pos": [j["default"] for j in self.joints],
            "lower": [j["lower"] for j in self.joints],
            "upper": [j["upper"] for j in self.joints],
            "kp": [self.gains[j["group"]][0] for j in self.joints],
            "kv": [self.gains[j["group"]][1] for j in self.joints],
            "force_limit": [self.gains[j["group"]][2] for j in self.joints],
            "action_scale": 0.5,
            "physics_hz": 200,
            "control_decimation": 4,
            "observation": ["base_lin_vel", "base_ang_vel", "projected_gravity", "target_command",
                            "joint_pos_rel", "joint_vel", "last_action"],
            "target_command": "unit direction (x, y) to target in the base yaw frame, plus min(distance, 10) / 10",
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
