"""
Decimated LOD copies of the White House, made from the exported glb — never from the owner's .blend.

    blender --background --python training/tools/whitehouse_lods.py -- Assets/Models/WhiteHouse.glb Assets/Models

Writes WhiteHouse_LOD1.glb and WhiteHouse_LOD2.glb beside the source, materials by name only (no
textures: Unity re-points every LOD renderer at the LOD0 material of the same name, so the 100 MB of
textures are shipped once). Object names are preserved, which is what BuildingLodBuilder matches on.

Why these ratios: the source is 833k triangles, of which the four building shells are 352k and the 38
trees are 400k. From the rooftop deck the building is always big on screen, so its LOD0 never leaves
on a desktop; the win is on the mobile tier, which skips LOD0 entirely (QualitySettings.maximumLODLevel).
LOD1 is therefore the phone's building and is cut to keep its silhouette; LOD2 is what the far wings and
the treeline become in a wide shot.
"""
import bpy, os, sys, time

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src = os.path.abspath(argv[0] if len(argv) > 0 else "Assets/Models/WhiteHouse.glb")
out_dir = os.path.abspath(argv[1] if len(argv) > 1 else os.path.dirname(src))
log_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "whitehouse_lods.log")
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


# LOD1 ratio, LOD2 ratio (None = deleted at that level), by object name prefix.
RULES = [
    ("Tree_",          0.20, 0.06),
    ("Wings",          0.35, 0.12),
    ("Residence",      0.35, 0.12),
    ("SouthPortico",   0.35, 0.12),
    ("NorthPortico",   0.35, 0.12),
    ("Grounds_Props",  0.30, None),
    ("Detail_Fixed",   0.30, None),
    ("Grounds_Hard",   1.00, 1.00),
    ("W_",             1.00, None),
    ("Car_",           1.00, None),
    ("Flag",           1.00, None),
]


def rule_for(name):
    for prefix, r1, r2 in RULES:
        if name.startswith(prefix):
            return r1, r2
    return 0.5, 0.15


t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
say(f"importing {src}")
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
say(f"imported {len(meshes)} meshes in {time.time() - t0:.0f} s")

before = sum(tri_count(o) for o in meshes)
say(f"source triangles {before}")

# One decimate modifier per mesh; the exporter applies modifiers, so the ratio is all a level changes.
for o in meshes:
    r1, _ = rule_for(o.name)
    if r1 < 1.0:
        m = o.modifiers.new("lod", "DECIMATE")
        m.decimate_type = "COLLAPSE"
        m.ratio = r1
        m.use_collapse_triangulate = True


def export(path):
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_apply=True,
        export_image_format="NONE",
        export_materials="EXPORT",
        export_texcoords=True,
        export_normals=True,
        export_tangents=False,
        export_yup=True,
        use_selection=False,
        export_animations=False,
        export_skins=False,
        export_morph=False,
        export_lights=False,
        export_cameras=False,
        export_extras=False,
    )


lod1 = os.path.join(out_dir, "WhiteHouse_LOD1.glb")
export(lod1)
after1 = sum(tri_count(o) for o in meshes)
say(f"LOD1 triangles {after1} ({100.0 * after1 / max(1, before):.0f}% of source) -> {lod1} "
    f"{os.path.getsize(lod1) / 1e6:.1f} MB at {time.time() - t0:.0f} s")

for o in list(meshes):
    _, r2 = rule_for(o.name)
    if r2 is None:
        bpy.data.objects.remove(o, do_unlink=True)
        meshes.remove(o)
        continue
    m = o.modifiers.get("lod")
    if m is not None:
        m.ratio = r2
    elif r2 < 1.0:
        m = o.modifiers.new("lod", "DECIMATE")
        m.decimate_type = "COLLAPSE"
        m.ratio = r2
        m.use_collapse_triangulate = True

lod2 = os.path.join(out_dir, "WhiteHouse_LOD2.glb")
export(lod2)
after2 = sum(tri_count(o) for o in meshes)
say(f"LOD2 triangles {after2} ({100.0 * after2 / max(1, before):.0f}% of source), {len(meshes)} meshes -> {lod2} "
    f"{os.path.getsize(lod2) / 1e6:.1f} MB at {time.time() - t0:.0f} s")
say("done")
log.close()
