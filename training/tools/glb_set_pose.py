"""
Stands a glb's skeleton in a given pose without touching its mesh or its skin.

    python training/tools/glb_set_pose.py Build/lod_pose.json [Assets/Models/Characters/Mobile]

Why this exists. A phone skin made from an FBX (athlete_lods.py) comes out of Blender in the pose its
mesh was bound in, which for an AccuRig model is the pose the scan was taken in: arms down, the dog on
all fours. Unity shows the same FBX in its T-pose take, and SkinBinder binds a skin to the physics rig
in whatever pose the prefab stands in. So the phone skin and the full model were bound in different
poses, and on the phone the dog ran with its arms out sideways. Blender cannot fix it (it evaluates the
take against a rest pose of its own and folds the mesh), so the pose is taken from Unity instead:
`training/tools/lod_pose_from_unity.cs`, run in the editor, writes where every bone of the phone skin
has to be for it to skin exactly as the full model does, and this writes those into the file.

Node translations and rotations change, and each posed joint's inverse bind matrix with them. The second
is what makes the phone skeleton the full model's skeleton rather than a skeleton that merely skins the
same: moving the joints alone reproduces the picture but leaves them in places only that skin's own bind
matrices explain, and SkinBinder sizes a skin from where its joints are. The mesh and the weights are
untouched.

The json is {"<file>.glb": {"<bone name>": {"p": [x, y, z], "q": [x, y, z, w], "bind": [16 floats]}}}:
p and q local to the bone's parent, bind the Unity bindpose, column-major, all in Unity's left-handed
convention (which is what glTFast produced from the file: x mirrored).
"""
import json
import os
import struct
import sys

pose_path = sys.argv[1] if len(sys.argv) > 1 else "Build/lod_pose.json"
glb_dir = sys.argv[2] if len(sys.argv) > 2 else "Assets/Models/Characters/Mobile"

poses = json.load(open(pose_path))
for name, bones in poses.items():
    path = os.path.join(glb_dir, name)
    data = open(path, "rb").read()
    magic, version, _ = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF" or version != 2:
        sys.exit(f"{path}: not a glTF 2 binary")
    json_len, json_type = struct.unpack_from("<I4s", data, 12)
    if json_type != b"JSON":
        sys.exit(f"{path}: first chunk is not JSON")
    doc = json.loads(data[20:20 + json_len])
    rest = bytearray(data[20 + json_len:])   # the binary chunk: only inverse bind matrices are rewritten
    bin_len, bin_type = struct.unpack_from("<I4s", rest, 0)
    if bin_type != b"BIN\x00":
        sys.exit(f"{path}: second chunk is not BIN")

    # Where each joint's inverse bind matrix lives, by node index.
    ibm_at = {}
    for skin in doc.get("skins", []):
        acc = doc["accessors"][skin["inverseBindMatrices"]]
        view = doc["bufferViews"][acc["bufferView"]]
        if acc["componentType"] != 5126 or acc["type"] != "MAT4" or view.get("byteStride", 64) != 64:
            sys.exit(f"{path}: inverse bind matrices are not plain float MAT4")
        base = 8 + view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        for k, node_index in enumerate(skin["joints"]):
            ibm_at.setdefault(node_index, []).append(base + 64 * k)

    moved, rebound, missing = 0, 0, []
    index_of = {n.get("name"): i for i, n in enumerate(doc["nodes"])}
    for bone, tr in bones.items():
        i = index_of.get(bone)
        if i is None:
            missing.append(bone)
            continue
        node = doc["nodes"][i]
        if "matrix" in node:
            sys.exit(f"{path}: node '{bone}' is stored as a matrix; cannot pose it")
        p, q = tr["p"], tr["q"]
        node["translation"] = [-p[0], p[1], p[2]]
        node["rotation"] = [q[0], -q[1], -q[2], q[3]]
        moved += 1
        if "bind" in tr:
            # Unity bindpose -> glTF: mirror x on both sides, which flips every element that has exactly
            # one of its row and column on x. Both are column-major.
            m = [v * (-1.0 if ((e % 4 == 0) != (e // 4 == 0)) else 1.0) for e, v in enumerate(tr["bind"])]
            for off in ibm_at.get(i, []):
                struct.pack_into("<16f", rest, off, *m)
                rebound += 1

    out = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    out += b" " * (-len(out) % 4)
    body = struct.pack("<I4s", len(out), b"JSON") + out + bytes(rest)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, 12 + len(body)) + body)
    print(f"{name}: {moved} bones posed, {rebound} bind matrices rewritten"
          + (f", {len(missing)} not in the file: {missing[:5]}" if missing else "")
          + f"; {len(data)} -> {12 + len(body)} bytes")
