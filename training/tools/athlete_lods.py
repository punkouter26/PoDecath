"""
Decimated copies of every rigged athlete, for the mobile tier. Made from the exported model files —
never from anyone's working .blend.

    blender --background --python training/tools/athlete_lods.py -- Assets/Models/Characters [out_dir] [budget] [tex_max]

Defaults: out_dir = <src>/Mobile, budget = 8000 triangles per athlete, tex_max = 1024 px.
Writes <name>_LOD1.glb per source file (glb and fbx both read; the output is always glb, which is what
glTFast imports and what AthleteRosterBuilder looks for under Mobile/). Log at training/logs/athlete_lods.log.

Why this exists. The White House got three levels of detail and the athletes got none: Trump is 25.7 MB,
the zombie and the dog 21.5 MB each, and up to sixteen of them run at once. On the mobile tier they are
the other half of a triangle budget the race scene is already 3.5x over. AthleteDefinition.skinOverrideMobile
takes the file this writes and AthleteSpawner draws it on a phone; a PC keeps the original.

What it preserves, because the runtime depends on it:
  - the armature and every vertex group. SkinBinder drives this skeleton from the physics rig, so the
    bones must be the same bones, by name, as the file the bone map was inferred from. Decimate (collapse)
    interpolates weights and never renames a group; the modifier is put *ahead* of the armature modifier
    so the exporter applies it to the bind pose, not to a deformed one.
  - object names and material names, so the roster and the rim/sheen shaders see what they saw before.
  - the textures, embedded as in the source, but capped at tex_max on their longest side. A 4k skin
    texture on a phone is memory the phone does not have, and at the size an athlete is drawn there it
    is indistinguishable from 1k.

What it does not do: it does not touch a model already under budget (the roster falls back to the
original, which is the right answer for a 6k-triangle glb), and it does not cut faces, eyes, teeth,
hair or tongues as hard as the body — a collapsed eyelid is the first thing a viewer notices and the
last thing a triangle count reports.
"""
import bpy, os, sys, time

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src_dir = os.path.abspath(argv[0] if len(argv) > 0 else "Assets/Models/Characters")
out_dir = os.path.abspath(argv[1] if len(argv) > 1 else os.path.join(src_dir, "Mobile"))
budget = int(argv[2]) if len(argv) > 2 else 8000
tex_max = int(argv[3]) if len(argv) > 3 else 1024
os.makedirs(out_dir, exist_ok=True)

log_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "athlete_lods.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


def tri_count(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(depsgraph)
    mesh = ev.to_mesh()
    n = sum(len(p.vertices) - 2 for p in mesh.polygons)
    ev.to_mesh_clear()
    return n


# Parts that get a gentler cut than the body, by a word in the object or material name. The face is
# where a decimation is judged; the body is where the triangles are.
DELICATE = ("head", "face", "eye", "teeth", "tooth", "tongue", "hair", "brow", "lash", "mouth", "lip")
DELICATE_FACTOR = 2.5     # ratio multiplier for delicate parts, capped at 1.0
SMALL_PART_TRIS = 600     # a part this small is not worth cutting; its share of the budget is noise


def is_delicate(obj):
    names = [obj.name.lower()] + [s.material.name.lower() for s in obj.material_slots if s.material]
    return any(word in n for n in names for word in DELICATE)


def put_decimate_first(obj, ratio):
    m = obj.modifiers.new("lod", "DECIMATE")
    m.decimate_type = "COLLAPSE"
    m.ratio = ratio
    m.use_collapse_triangulate = True
    m.use_symmetry = False
    # Ahead of the armature modifier, so the cut is made on the bind pose. Blender applies modifiers in
    # stack order and the glTF exporter applies them all; a decimate *after* the armature would be cut on
    # whatever pose the file was saved in.
    idx = list(obj.modifiers).index(m)
    if idx > 0:
        obj.modifiers.move(idx, 0)


def cap_textures(limit):
    shrunk = 0
    for img in bpy.data.images:
        if img.size[0] <= 0 or img.size[1] <= 0:
            continue
        w, h = img.size
        big = max(w, h)
        if big <= limit:
            continue
        s = limit / big
        img.scale(max(1, int(w * s)), max(1, int(h * s)))
        shrunk += 1
    return shrunk


def import_model(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path, use_anim=False)
    else:
        raise ValueError(ext)


def export_glb(path):
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_apply=True,
        export_image_format="AUTO",
        export_materials="EXPORT",
        export_texcoords=True,
        export_normals=True,
        export_tangents=False,
        export_yup=True,
        use_selection=False,
        # The physics rig drives the bones; no clip in the file is ever played. Skins stay: they are the
        # whole point.
        export_animations=False,
        export_skins=True,
        export_morph=False,
        export_lights=False,
        export_cameras=False,
        export_extras=False,
    )


sources = sorted(f for f in os.listdir(src_dir)
                 if os.path.splitext(f)[1].lower() in (".glb", ".gltf", ".fbx")
                 and "_LOD" not in f
                 and os.path.isfile(os.path.join(src_dir, f)))
say(f"{len(sources)} models in {src_dir}; budget {budget} triangles, textures capped at {tex_max} px; out {out_dir}")

t_all = time.time()
for f in sources:
    t0 = time.time()
    path = os.path.join(src_dir, f)
    stem = os.path.splitext(f)[0]
    out = os.path.join(out_dir, f"{stem}_LOD1.glb")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        import_model(path)
    except Exception as e:
        say(f"{f}: import failed ({e}); skipped")
        continue

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    arm = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    before = sum(tri_count(o) for o in meshes)
    if not arm:
        say(f"{f}: no armature in the file — this is not a rigged athlete; skipped")
        continue
    if before <= budget:
        say(f"{f}: {before} triangles is already under budget; no copy made, the roster uses the original")
        continue

    # One ratio across the body so proportions hold, then the delicate parts and the tiny parts are
    # spared. The body absorbs the difference; it has the triangles to give.
    ratio = budget / before
    cut, spared = 0, 0
    for o in meshes:
        n = tri_count(o)
        if n <= SMALL_PART_TRIS:
            spared += 1
            continue
        r = min(1.0, ratio * DELICATE_FACTOR) if is_delicate(o) else ratio
        if r < 1.0:
            put_decimate_first(o, r)
            cut += 1
        else:
            spared += 1

    shrunk = cap_textures(tex_max)
    export_glb(out)
    after = sum(tri_count(o) for o in meshes)
    say(f"{f}: {before} -> {after} triangles ({100.0 * after / max(1, before):.0f}%), "
        f"{cut} parts cut, {spared} spared, {shrunk} textures shrunk, "
        f"{os.path.getsize(path) / 1e6:.1f} -> {os.path.getsize(out) / 1e6:.1f} MB in {time.time() - t0:.0f} s -> {out}")

say(f"done in {time.time() - t_all:.0f} s. Now run PoDecath/Rebuild Athlete Roster so each AthleteDefinition picks up its Mobile/ skin.")
log.close()
