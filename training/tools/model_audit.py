"""
Audit exported models (glb/fbx) against the game-ready checklist before touching them.

    blender-launcher --background --python training/tools/model_audit.py -- <out.json> <model> [<model> ...]

Reads each file into an empty scene and reports, per file and per mesh: triangles, ngons, open/non-manifold
edges, transforms that are not applied (rotation, negative or non-uniform scale), where the origin sits
relative to the mesh's feet, UV layers and how much of UV0 falls outside 0..1, vertex colours, bone
influences above four, and per material which PBR maps are wired and in which colour space, plus any
procedural texture node that would not survive export. Texel density is pixels per metre of the base-colour
map. Nothing is written except the JSON report; the models are never saved.
"""
import bpy, bmesh, json, math, os, sys, time
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out_path, models = os.path.abspath(argv[0]), [os.path.abspath(p) for p in argv[1:]]

PBR_INPUTS = ["Base Color", "Metallic", "Roughness", "Normal", "Emission Color", "Alpha"]
PROCEDURAL = {"TEX_NOISE", "TEX_VORONOI", "TEX_WAVE", "TEX_MUSGRAVE", "TEX_GRADIENT", "TEX_MAGIC",
              "TEX_CHECKER", "TEX_BRICK", "TEX_WHITE_NOISE", "TEX_GABOR", "AMBIENT_OCCLUSION", "BEVEL"}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path):
    if path.lower().endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        bpy.ops.import_scene.gltf(filepath=path)


def upstream_image(sock, seen=None):
    """First image texture feeding a socket, walking through normal-map / separate / math nodes."""
    seen = seen or set()
    for link in sock.links:
        n = link.from_node
        if n.name in seen:
            continue
        seen.add(n.name)
        if n.type == "TEX_IMAGE" and n.image:
            return n.image
        for s in n.inputs:
            img = upstream_image(s, seen)
            if img:
                return img
    return None


def material_report(mat):
    r = {"name": mat.name, "maps": {}, "procedural": [], "blend": getattr(mat, "surface_render_method", "")}
    if not mat.use_nodes or not mat.node_tree:
        r["no_nodes"] = True
        return r
    nodes = mat.node_tree.nodes
    r["procedural"] = sorted({n.type for n in nodes if n.type in PROCEDURAL})
    bsdf = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
    if not bsdf:
        r["no_principled"] = True
        return r
    for name in PBR_INPUTS:
        s = bsdf.inputs.get(name)
        if s is None:
            continue
        img = upstream_image(s)
        if img:
            r["maps"][name] = {"image": img.name, "size": list(img.size), "colorspace": img.colorspace_settings.name}
        elif name in ("Metallic", "Roughness", "Alpha"):
            r["maps"][name] = {"value": round(float(s.default_value), 3)}
        elif name == "Base Color":
            r["maps"][name] = {"value": [round(float(c), 3) for c in s.default_value[:3]]}
    em = bsdf.inputs.get("Emission Strength")
    if em is not None:
        r["emission_strength"] = round(float(em.default_value), 3)
    # glTF occlusion lives in the importer's "glTF Material Output" group, not on the BSDF.
    for n in nodes:
        if n.type == "GROUP" and n.node_tree and "glTF" in n.node_tree.name:
            s = n.inputs.get("Occlusion")
            img = upstream_image(s) if s else None
            if img:
                r["maps"]["Occlusion"] = {"image": img.name, "size": list(img.size),
                                          "colorspace": img.colorspace_settings.name}
    return r


def mesh_report(obj, deps):
    me = obj.data
    r = {"name": obj.name, "verts": len(me.vertices)}
    loc, rot, scl = obj.matrix_world.decompose()
    s = list(scl)
    r["scale"] = [round(v, 4) for v in s]
    r["negative_scale"] = obj.matrix_world.determinant() < 0
    r["nonuniform_scale"] = max(abs(v) for v in s) - min(abs(v) for v in s) > 1e-3
    r["rotated"] = abs(rot.angle) > 1e-3
    r["unapplied_scale"] = any(abs(abs(v) - 1) > 1e-3 for v in s)

    npoly = len(me.polygons)
    sizes = np.empty(npoly, dtype=np.int32)
    me.polygons.foreach_get("loop_total", sizes)
    r["tris"] = int((sizes - 2).sum())
    r["ngons"] = int((sizes > 4).sum())
    r["quads"] = int((sizes == 4).sum())

    bm = bmesh.new()
    bm.from_mesh(me)
    r["boundary_edges"] = sum(1 for e in bm.edges if e.is_boundary)
    r["nonmanifold_edges"] = sum(1 for e in bm.edges if not e.is_manifold and not e.is_boundary)
    r["loose_verts"] = sum(1 for v in bm.verts if not v.link_edges)
    r["degenerate_faces"] = sum(1 for f in bm.faces if f.calc_area() < 1e-10)
    world_area = sum(f.calc_area() for f in bm.faces) * abs(obj.matrix_world.determinant()) ** (2 / 3)
    bm.free()
    r["area_m2"] = round(world_area, 3)

    # Origin relative to the mesh's world bounding box: a "ground pivot" sits at min Z.
    co = np.empty(len(me.vertices) * 3, dtype=np.float32)
    me.vertices.foreach_get("co", co)
    if len(co):
        co = co.reshape(-1, 3)
        mw = np.array(obj.matrix_world)
        w = co @ mw[:3, :3].T + mw[:3, 3]
        mn, mx = w.min(0), w.max(0)
        r["bbox_m"] = [round(float(v), 3) for v in (mx - mn)]
        r["origin_above_min_z"] = round(float(mw[2, 3] - mn[2]), 3)

    r["uv_layers"] = [uv.name for uv in me.uv_layers]
    if me.uv_layers:
        uv = np.empty(len(me.loops) * 2, dtype=np.float32)
        me.uv_layers[0].data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2)
        out = ((uv < -1e-4) | (uv > 1 + 1e-4)).any(1)
        r["uv0_outside_01"] = round(float(out.mean()), 3) if len(uv) else 0
        # UV area of UV0 (for texel density)
        tri_uv_area = 0.0
        me.calc_loop_triangles()
        lt = np.empty(len(me.loop_triangles) * 3, dtype=np.int32)
        me.loop_triangles.foreach_get("loops", lt)
        t = uv[lt].reshape(-1, 3, 2)
        a = t[:, 1] - t[:, 0]
        b = t[:, 2] - t[:, 0]
        tri_uv_area = float(np.abs(a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0]).sum() / 2)
        r["uv0_area"] = round(tri_uv_area, 4)
    r["vertex_colors"] = [a.name for a in me.color_attributes]
    r["custom_normals"] = me.has_custom_normals
    r["materials"] = [m.name for m in me.materials if m]

    arm = next((m for m in obj.modifiers if m.type == "ARMATURE"), None)
    if arm or obj.vertex_groups:
        counts = np.zeros(len(me.vertices), dtype=np.int32)
        for i, v in enumerate(me.vertices):
            counts[i] = sum(1 for g in v.groups if g.weight > 1e-4)
        r["max_influences"] = int(counts.max()) if len(counts) else 0
        r["verts_over_4_influences"] = int((counts > 4).sum())
        r["unweighted_verts"] = int((counts == 0).sum())
    return r


report = {"blender": bpy.app.version_string, "files": []}
for path in models:
    t0 = time.time()
    reset()
    entry = {"file": path, "bytes": os.path.getsize(path)}
    try:
        load(path)
    except Exception as ex:
        entry["error"] = repr(ex)
        report["files"].append(entry)
        continue
    sc = bpy.context.scene
    entry["unit_system"] = sc.unit_settings.system
    entry["unit_scale"] = sc.unit_settings.scale_length
    deps = bpy.context.evaluated_depsgraph_get()
    meshes = [o for o in sc.objects if o.type == "MESH"]
    entry["objects"] = len(sc.objects)
    entry["armatures"] = [o.name for o in sc.objects if o.type == "ARMATURE"]
    entry["lights"] = len([o for o in sc.objects if o.type == "LIGHT"])
    entry["cameras"] = len([o for o in sc.objects if o.type == "CAMERA"])
    entry["meshes"] = [mesh_report(o, deps) for o in meshes]
    mats = {m.name: m for o in meshes for m in o.data.materials if m}
    entry["materials"] = [material_report(m) for m in mats.values()]
    entry["images"] = [{"name": i.name, "size": list(i.size), "colorspace": i.colorspace_settings.name,
                        "packed": bool(i.packed_file)} for i in bpy.data.images if i.size[0]]

    # Texel density per mesh: base-colour pixels per metre.
    bc = {m["name"]: m["maps"].get("Base Color", {}).get("size") for m in entry["materials"]}
    for mr in entry["meshes"]:
        sz = next((bc[n] for n in mr["materials"] if bc.get(n)), None)
        if sz and mr.get("uv0_area") and mr["area_m2"] > 0:
            mr["texel_px_per_m"] = round(math.sqrt(mr["uv0_area"] * sz[0] * sz[1] / mr["area_m2"]), 1)
    entry["seconds"] = round(time.time() - t0, 1)
    report["files"].append(entry)
    with open(out_path, "w") as f:
        json.dump(report, f, indent=1)

report["done"] = True
with open(out_path, "w") as f:
    json.dump(report, f, indent=1)
