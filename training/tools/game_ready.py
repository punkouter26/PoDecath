"""
Game-ready pass over the exported White House glb: the parts of the export checklist that are real fixes
for this file, and nothing that would undo how it was built. Reads the glb (never the owner's .blend).

    blender-launcher --background --python training/tools/game_ready.py -- <in.glb> <out.glb>

What it does, and why each step is limited the way it is:
  * Units: metric, scale 1.0 (already so; asserted).
  * Transforms: *not* applied. 314 objects share 28 meshes (107 wing windows are one mesh), and every
    rotation and every uniform tree scale is an instance placement. Applying them would turn 28 meshes
    into 314 and grow the file and the phone's memory for no visual change. There is no negative or
    non-uniform scale to remove (model_audit.py). The step that does matter is below: pivots.
  * Pivots: Flagpole and Flag had their origins on the lawn, 22 m and 33 m below them. The pole now pivots
    at its foot on the roof and the flag at its top hoist corner, next to the pole, which is where it
    would swing from. World geometry does not move.
  * Normals: faces whose winding disagrees with their own shading normal are flipped back (the materials
    are double-sided, so these light from the wrong side rather than vanish). Hard-surface parts get a
    Weighted Normal pass (face-area weighted, existing hard edges kept), so big flat walls stop shading
    as if they were curved where they meet a small bevel. Trees and people keep their normals.
  * Clean-up: zero-area faces and loose vertices are counted (the shipped export has none).
  * Export: the recipe in DOCS/BLENDER_EXPORT.md (glb, +Y up, modifiers applied, no tangents, images
    automatic, no animation). Object and material names are untouched; the LOD pipeline keys on them.
Stats go to training/logs/game_ready.log.
"""
import bpy, bmesh, os, sys, time
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src, dst = os.path.abspath(argv[0]), os.path.abspath(argv[1])
log_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "game_ready.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


# Hard-surface parts get weighted normals; organic ones keep what the artist gave them.
HARD = ("Residence", "Wings", "SouthPortico", "NorthPortico", "W_", "Grounds_Hard", "Grounds_Props",
        "Detail_Fixed", "Car_", "Lamp", "Bollard", "GuardBooth", "Flagpole")

t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
sc = bpy.context.scene
sc.unit_settings.system = "METRIC"
sc.unit_settings.scale_length = 1.0
meshes = [o for o in sc.objects if o.type == "MESH"]
say(f"loaded {src}: {len(meshes)} mesh objects, {len({o.data for o in meshes})} meshes, {time.time() - t0:.1f}s")


def corner_normals(me):
    n = np.empty(len(me.loops) * 3, dtype=np.float32)
    me.corner_normals.foreach_get("vector", n)
    return n.reshape(-1, 3)


# --- Pivots ---------------------------------------------------------------------------------------
def set_origin(obj, world_point):
    """Move the object's origin to world_point without moving its geometry (single-user mesh only)."""
    mw = obj.matrix_world
    local = mw.inverted() @ world_point
    obj.data.transform(__import__("mathutils").Matrix.Translation(-local))
    obj.matrix_world = mw @ __import__("mathutils").Matrix.Translation(local)


def world_bbox(obj):
    pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    return (Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))),
            Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))))


pole = sc.objects.get("Flagpole")
flag = sc.objects.get("Flag")
if pole and pole.data.users == 1:
    lo, hi = world_bbox(pole)
    foot = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    before = pole.matrix_world.translation.copy()
    set_origin(pole, foot)
    say(f"Flagpole pivot {tuple(round(v, 2) for v in before)} -> foot {tuple(round(v, 2) for v in foot)}")
    if flag and flag.data.users == 1:
        flo, fhi = world_bbox(flag)
        hoist = Vector((foot.x, foot.y, fhi.z))
        # The hoist corner is the flag vertex nearest the pole axis at the top.
        verts = [flag.matrix_world @ v.co for v in flag.data.vertices]
        best = min(verts, key=lambda p: (Vector((p.x, p.y, 0)) - Vector((foot.x, foot.y, 0))).length - 0.01 * p.z)
        before = flag.matrix_world.translation.copy()
        set_origin(flag, best)
        say(f"Flag pivot {tuple(round(v, 2) for v in before)} -> hoist {tuple(round(v, 2) for v in best)}")

# --- Normals and clean-up, once per unique mesh --------------------------------------------------
done = set()
flipped_total = removed_total = loose_total = 0
wn_meshes = []
for obj in meshes:
    me = obj.data
    if me in done:
        continue
    done.add(me)
    had_custom = me.has_custom_normals
    cn = corner_normals(me)

    # Faces whose winding points against their own corner normals.
    pn = np.empty(len(me.polygons) * 3, dtype=np.float32)
    me.polygons.foreach_get("normal", pn)
    pn = pn.reshape(-1, 3)
    starts = np.empty(len(me.polygons), dtype=np.int32)
    totals = np.empty(len(me.polygons), dtype=np.int32)
    me.polygons.foreach_get("loop_start", starts)
    me.polygons.foreach_get("loop_total", totals)
    sums = np.add.reduceat(cn, starts, axis=0) if len(starts) else np.zeros((0, 3))
    bad = np.where((pn * sums).sum(1) < -0.5 * totals)[0]
    if len(bad):
        # Flip the winding but keep each corner's shading normal: remember normal by (face, vertex).
        keep = {}
        for pi in bad:
            p = me.polygons[pi]
            for li in range(p.loop_start, p.loop_start + p.loop_total):
                keep[(pi, me.loops[li].vertex_index)] = cn[li].copy()
        for pi in bad:
            me.polygons[pi].flip()
        new = cn.copy()
        for pi in bad:
            p = me.polygons[pi]
            for li in range(p.loop_start, p.loop_start + p.loop_total):
                new[li] = keep[(pi, me.loops[li].vertex_index)]
        me.normals_split_custom_set(new.tolist())
        flipped_total += len(bad)
        say(f"  {me.name}: flipped {len(bad)} inverted faces")

    # Zero-area faces and loose vertices: counted, not repaired. The shipped export has none
    # (model_audit.py); a non-zero count here means the source changed and deserves a look.
    bm = bmesh.new()
    bm.from_mesh(me)
    degen = sum(1 for f in bm.faces if f.calc_area() < 1e-10)
    loose = sum(1 for v in bm.verts if not v.link_edges)
    bm.free()
    if degen or loose:
        removed_total += degen
        loose_total += loose
        say(f"  {me.name}: {degen} zero-area faces, {loose} loose verts (left in place)")

    if obj.name.startswith(HARD) or me.name.startswith(HARD) or any(h in me.name for h in ("Portico", "Residence", "Wings")):
        wn_meshes.append(me)

# Weighted Normal, applied on a single-user stand-in per mesh so instanced windows keep sharing one mesh.
angle_stats = []
for old in wn_meshes:
    # A private copy takes the modifier, then replaces the original under every object that used it.
    users = [o for o in meshes if o.data == old]
    before = corner_normals(old)
    me = old.copy()
    tmp = bpy.data.objects.new("_wn_tmp", me)
    sc.collection.objects.link(tmp)
    mod = tmp.modifiers.new("WN", "WEIGHTED_NORMAL")
    mod.mode = "FACE_AREA"
    mod.weight = 50
    mod.keep_sharp = True
    mod.thresh = 0.01
    bpy.context.view_layer.objects.active = tmp
    for o in sc.objects:
        o.select_set(o == tmp)
    with bpy.context.temp_override(object=tmp, active_object=tmp, selected_objects=[tmp]):
        bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(tmp)
    for o in users:
        o.data = me
    name = old.name
    bpy.data.meshes.remove(old)
    me.name = name
    after = corner_normals(me)
    dots = np.clip((before * after).sum(1), -1, 1)
    ang = np.degrees(np.arccos(dots))
    angle_stats.append((me.name, len(ang), float(np.mean(ang)), float(np.percentile(ang, 95)), float((ang > 5).mean())))
    say(f"  WN {me.name}: corners {len(ang)}, mean change {np.mean(ang):.2f} deg, 95th pct {np.percentile(ang, 95):.2f} deg, "
        f"{100 * (ang > 5).mean():.1f}% moved > 5 deg")

say(f"flipped {flipped_total} faces; {removed_total} zero-area faces and {loose_total} loose verts found; "
    f"weighted normals on {len(wn_meshes)} meshes")

os.makedirs(os.path.dirname(dst), exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=dst, export_format="GLB", export_yup=True, export_apply=True,
    export_texcoords=True, export_normals=True, export_tangents=False,
    export_materials="EXPORT", export_image_format="AUTO", export_animations=False,
    export_draco_mesh_compression_enable=False)
say(f"wrote {dst} ({os.path.getsize(dst) / 1e6:.1f} MB) in {time.time() - t0:.0f}s")
say("DONE")
log.close()
