"""
Fix the material and skinning faults that make imported athletes look unlit, metallic or torn, by patching
the glb in place. The mesh, the skeleton and every original texture stay byte-for-byte as they were: the
owner's house rule is that athletes keep their imported textures, so nothing here re-exports the model.

    blender-launcher --background --python training/tools/athlete_glb_fix.py -- <in.glb> <out.glb> [--ao]

Faults fixed (all found by model_audit.py in the shipped roster, 2026-09-29):
  * Metallic by omission. glTF's default metallicFactor is 1.0, so a material that states no metallic value
    and has no metallic/roughness map is solid metal (the AI-generated Grandma, Grandpa and Nick). It gets
    metallicFactor 0 and roughnessFactor 0.65, which is skin and cloth.
  * Glowing with its own colour. An emissive texture that is the base-colour image at full strength makes
    the model light itself, so it looks flat and ignores the sun and shadows. It is removed.
  * Doubled specular. KHR_materials_specular at 2.0 is twice the physical reflectance; the extension is
    dropped so the default (1.0) applies.
  * More than four bone influences. glTFast reads JOINTS_0/WEIGHTS_0 only, so the extra set is ignored and
    the remaining weights no longer sum to one: those vertices shrink toward the root. The four strongest are
    kept and renormalised, and the extra set is removed.
  * Flat-shaded export. One normal per triangle shows every facet once the model is lit. NORMAL is rewritten
    as the smooth average around each point (75 deg crease limit); positions, UVs and weights are untouched.
  * --ao: no ambient-occlusion map. One is baked in Cycles (1k, 0.12 m reach: contact shadow in creases,
    armpits and under clothing, not a darkening of the whole body) and added as the occlusion texture.
Writes a line per fix to stdout and training/logs/athlete_glb_fix.log.
"""
import bpy, json, os, struct, sys, tempfile
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src, dst = os.path.abspath(argv[0]), os.path.abspath(argv[1])
bake_ao = "--ao" in argv
log_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "athlete_glb_fix.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "a")


def say(msg):
    msg = f"{os.path.basename(src)}: {msg}"
    log.write(msg + "\n")
    log.flush()
    print(msg)


# --- glb read/write ------------------------------------------------------------------------------
def read_glb(path):
    b = open(path, "rb").read()
    magic, ver, total = struct.unpack("<III", b[:12])
    assert magic == 0x46546C67, "not a glb"
    off, j, binc = 12, None, b""
    while off < total:
        ln, typ = struct.unpack("<II", b[off:off + 8])
        chunk = b[off + 8:off + 8 + ln]
        if typ == 0x4E4F534A:
            j = json.loads(chunk)
        elif typ == 0x004E4942:
            binc = bytearray(chunk)
        off += 8 + ln
    return j, binc


def write_glb(path, j, binc):
    js = json.dumps(j, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    binc = bytes(binc) + b"\0" * (-len(binc) % 4)
    j_len = 12 + 8 + len(js) + 8 + len(binc)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, j_len))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(binc), 0x004E4942) + binc)


COMP = {5121: np.uint8, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def accessor_view(j, binc, ai):
    """(array view shaped [count, n], writer) for an accessor, respecting byteStride."""
    a = j["accessors"][ai]
    bv = j["bufferViews"][a["bufferView"]]
    dt = np.dtype(COMP[a["componentType"]])
    n = NCOMP[a["type"]]
    start = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = bv.get("byteStride", dt.itemsize * n)
    count = a["count"]
    raw = np.frombuffer(binc, dtype=np.uint8)
    rows = np.lib.stride_tricks.as_strided(raw[start:], shape=(count, dt.itemsize * n), strides=(stride, 1))
    arr = rows.copy().view(dt).reshape(count, n)

    def write(values):
        vals = np.ascontiguousarray(values.astype(dt)).view(np.uint8).reshape(count, dt.itemsize * n)
        for i in range(count):
            binc[start + i * stride:start + i * stride + dt.itemsize * n] = vals[i].tobytes()
    return arr, write, a


def as_unit(arr, a):
    if a["componentType"] == 5126:
        return arr.astype(np.float64)
    return arr.astype(np.float64) / np.iinfo(COMP[a["componentType"]]).max


def from_unit(vals, a):
    if a["componentType"] == 5126:
        return vals
    mx = np.iinfo(COMP[a["componentType"]]).max
    q = np.round(vals * mx)
    # keep the quantised sum exact: put the rounding error on each vertex's largest weight
    err = mx - q.sum(1)
    q[np.arange(len(q)), q.argmax(1)] += err
    return q


j, binc = read_glb(src)
tex_image = lambda ti: j["textures"][ti].get("source") if ti is not None and ti < len(j.get("textures", [])) else None
changed = []

# --- Materials -----------------------------------------------------------------------------------
for m in j.get("materials", []):
    pbr = m.setdefault("pbrMetallicRoughness", {})
    name = m.get("name", "?")
    if "metallicRoughnessTexture" not in pbr and pbr.get("metallicFactor", 1.0) > 0.5:
        was = (pbr.get("metallicFactor", 1.0), pbr.get("roughnessFactor", 1.0))
        pbr["metallicFactor"] = 0.0
        pbr["roughnessFactor"] = 0.65
        changed.append(f"{name}: metallic {was[0]} -> 0, roughness {was[1]} -> 0.65 (no map; glTF default was solid metal)")
    et = m.get("emissiveTexture", {}).get("index")
    bt = pbr.get("baseColorTexture", {}).get("index")
    if et is not None and bt is not None and tex_image(et) == tex_image(bt):
        m.pop("emissiveTexture", None)
        factor = m.pop("emissiveFactor", [0, 0, 0])
        changed.append(f"{name}: removed self-glow (emissive = its own base colour x {factor})")
    ext = m.get("extensions", {})
    sp = ext.get("KHR_materials_specular", {})
    if max(sp.get("specularColorFactor", [1, 1, 1])) > 1.0 and "specularColorTexture" not in sp and "specularTexture" not in sp:
        ext.pop("KHR_materials_specular")
        if not ext:
            m.pop("extensions", None)
        changed.append(f"{name}: specular {sp.get('specularColorFactor')} -> default 1.0")
used = {e for m in j.get("materials", []) for e in m.get("extensions", {})}
for key in ("extensionsUsed", "extensionsRequired"):
    if key in j:
        j[key] = [e for e in j[key] if e in used or not e.startswith("KHR_materials_")]
        if not j[key]:
            j.pop(key)

# --- Skin weights --------------------------------------------------------------------------------
for mesh in j.get("meshes", []):
    for prim in mesh["primitives"]:
        at = prim["attributes"]
        if "JOINTS_1" not in at:
            continue
        j0, w_j0, a_j0 = accessor_view(j, binc, at["JOINTS_0"])
        j1, _, _ = accessor_view(j, binc, at["JOINTS_1"])
        w0, w_w0, a_w0 = accessor_view(j, binc, at["WEIGHTS_0"])
        w1, _, a_w1 = accessor_view(j, binc, at["WEIGHTS_1"])
        joints = np.concatenate([j0, j1], 1)
        weights = np.concatenate([as_unit(w0, a_w0), as_unit(w1, a_w1)], 1)
        over = int(((weights > 1e-4).sum(1) > 4).sum())
        lost_before = float((1 - as_unit(w0, a_w0).sum(1)).max())
        order = np.argsort(-weights, 1)[:, :4]
        rows = np.arange(len(weights))[:, None]
        kj, kw = joints[rows, order], weights[rows, order]
        s = kw.sum(1, keepdims=True)
        s[s == 0] = 1
        kw = kw / s
        w_j0(kj)
        w_w0(from_unit(kw, a_w0))
        at.pop("JOINTS_1")
        at.pop("WEIGHTS_1")
        changed.append(f"mesh {mesh.get('name')}: {over} vertices had >4 bones; kept strongest 4, renormalised "
                       f"(first set alone was short by up to {lost_before:.0%})")

# --- Flat-shaded export --------------------------------------------------------------------------
# Some generators write one normal per triangle. Lit by a sun, every triangle then shows as a facet; the
# self-glow above used to hide it. Detect it (normals at shared positions disagree by a median > 15 deg)
# and rewrite NORMAL as the area-weighted average of the faces around each position, skipping any face more
# than 75 deg off the vertex's own normal so a sleeve's inside and outside, or a sole's edge, stay apart.
COS_LIMIT = np.cos(np.radians(75))
for mesh in j.get("meshes", []):
    for prim in mesh["primitives"]:
        at = prim["attributes"]
        if "NORMAL" not in at or prim.get("mode", 4) != 4:
            continue
        P, _, _ = accessor_view(j, binc, at["POSITION"])
        N, write_n, a_n = accessor_view(j, binc, at["NORMAL"])
        if "indices" in prim:
            idx, _, _ = accessor_view(j, binc, prim["indices"])
            tri = idx.reshape(-1, 3).astype(np.int64)
        else:
            tri = np.arange(len(P)).reshape(-1, 3)
        key = np.round(P.astype(np.float64), 5)
        _, pos_id = np.unique(key, axis=0, return_inverse=True)
        pos_id = pos_id.ravel()
        # how split are the normals at shared positions?
        order = np.argsort(pos_id, kind="stable")
        same = pos_id[order][1:] == pos_id[order][:-1]
        d = (N[order][1:] * N[order][:-1]).sum(1)[same]
        if len(d) == 0 or np.degrees(np.arccos(np.clip(np.median(d), -1, 1))) < 15:
            continue
        v0, v1, v2 = (P[tri[:, k]].astype(np.float64) for k in range(3))
        fn = np.cross(v1 - v0, v2 - v0)                  # length = 2 x area, i.e. area-weighted
        fu = fn / np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-20)
        # faces touching each position
        corner_pos = pos_id[tri].ravel()
        corner_face = np.repeat(np.arange(len(tri)), 3)
        by_pos = np.argsort(corner_pos, kind="stable")
        cp, cf = corner_pos[by_pos], corner_face[by_pos]
        bounds = np.searchsorted(cp, np.arange(pos_id.max() + 2))
        newN = N.astype(np.float64).copy()
        for v in range(len(P)):
            p = pos_id[v]
            faces = np.unique(cf[bounds[p]:bounds[p + 1]])
            if len(faces) == 0:
                continue
            ok = faces[(fu[faces] @ N[v].astype(np.float64)) > COS_LIMIT]
            if len(ok) == 0:
                continue
            s = fn[ok].sum(0)
            ln = np.linalg.norm(s)
            if ln > 1e-20:
                newN[v] = s / ln
        med_before = np.degrees(np.arccos(np.clip(np.median(d), -1, 1)))
        d2 = (newN[order][1:] * newN[order][:-1]).sum(1)[same]
        med_after = np.degrees(np.arccos(np.clip(np.median(d2), -1, 1)))
        write_n(newN.astype(np.float32))
        changed.append(f"mesh {mesh.get('name')}: flat-shaded export smoothed (median angle between normals at a "
                       f"shared point {med_before:.0f} deg -> {med_after:.0f} deg, 75 deg crease limit)")

# --- Ambient occlusion ---------------------------------------------------------------------------
if bake_ao:
    targets = [i for i, m in enumerate(j.get("materials", [])) if "occlusionTexture" not in m]
    if targets:
        # Bake from the patched file, so the occlusion follows the smoothed normals and not the facets.
        patched = os.path.join(tempfile.gettempdir(), f"patched_{os.getpid()}.glb")
        write_glb(patched, j, binc)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=patched)
        os.remove(patched)
        sc = bpy.context.scene
        sc.render.engine = "CYCLES"
        sc.cycles.device = "CPU"
        sc.cycles.samples = 96
        sc.world = sc.world or bpy.data.worlds.new("W")
        sc.world.light_settings.distance = 0.12
        img = bpy.data.images.new("AO", 1024, 1024, alpha=False)
        img.generated_color = (1, 1, 1, 1)
        img.colorspace_settings.name = "Non-Color"
        mats = {j["materials"][i]["name"] for i in targets}
        objs = [o for o in sc.objects if o.type == "MESH" and any(ms.material and ms.material.name in mats for ms in o.material_slots)]
        for o in objs:
            for ms in o.material_slots:
                if ms.material and ms.material.name in mats:
                    nt = ms.material.node_tree
                    n = nt.nodes.new("ShaderNodeTexImage")
                    n.image = img
                    nt.nodes.active = n
        for o in sc.objects:
            o.select_set(o in objs)
        bpy.context.view_layer.objects.active = objs[0]
        bpy.ops.object.bake(type="AO", margin=8, use_clear=True)
        tmp = os.path.join(tempfile.gettempdir(), f"ao_{os.getpid()}.png")
        img.filepath_raw = tmp
        img.file_format = "PNG"
        img.save()
        px = np.array(img.pixels[:]).reshape(-1, 4)[:, 0]
        png = open(tmp, "rb").read()
        os.remove(tmp)
        # append the PNG to the binary chunk and point every target material at it
        pad = -len(binc) % 4
        binc.extend(b"\0" * pad)
        j["bufferViews"].append({"buffer": 0, "byteOffset": len(binc), "byteLength": len(png)})
        binc.extend(png)
        j["buffers"][0]["byteLength"] = len(binc)
        j.setdefault("images", []).append({"name": "AO_baked", "mimeType": "image/png", "bufferView": len(j["bufferViews"]) - 1})
        j.setdefault("textures", []).append({"source": len(j["images"]) - 1})
        for i in targets:
            j["materials"][i]["occlusionTexture"] = {"index": len(j["textures"]) - 1, "strength": 1.0}
        changed.append(f"baked AO 1024px onto {sorted(mats)} (mean {px.mean():.2f}, darkest 5% at {np.percentile(px, 5):.2f})")

for c in changed:
    say(c)
if not changed:
    say("nothing to fix")
write_glb(dst, j, binc)
say(f"wrote {dst} ({os.path.getsize(dst) / 1e6:.1f} MB)")
