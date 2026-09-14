"""
Bake the full-detail building's surface onto its cheapest shell, so a phone drawing LOD2 still sees
columns, window reveals and mouldings — painted on, at the cost of two textures instead of 300k triangles.

    blender --background --python training/tools/whitehouse_facade_bake.py -- Assets/Models/WhiteHouse.glb Assets/Models/WhiteHouse_LOD2.glb Assets/Textures/Building [size]

Default size 2048. Reads the exported glbs — never the owner's .blend. For each building shell in SHELLS
it writes <part>_bakeN.png (tangent-space normal) and <part>_bakeD.png (albedo, lighting excluded) and a
new Assets/Models/WhiteHouse_LOD2.glb carrying a non-overlapping "Bake" UV map as its first UV set, so
the textures line up with the mesh Unity imports. BuildingLodBuilder assigns them: a part with a baked
pair under Assets/Textures/Building gets its own material at LOD2 instead of the LOD0 one.

Why bake rather than just cut harder. The 2026-09-12 sweep has the race scene at 712k triangles on the
mobile tier against a 200k target, and moving the phone from LOD1 to LOD2 (RenderTier.MobileLodCeiling)
is the biggest lever left. LOD2 is a decimated shell, and decimation keeps silhouette and loses relief:
the pilasters and cornices that make the building read as carved are the first thing a collapse throws
away. A normal map puts that relief back for the price of a texture fetch, which is the trade a phone
wants.

Blender specifics worth knowing before believing the output:
  - selected-to-active bake, high (LOD0 part) selected, low (LOD2 part) active, cage extrusion 0.25 m.
    The building is in metres; 0.25 clears every reveal on the facade and no window is deeper than that.
  - the low mesh gets a fresh smart-projected UV for the bake; its original UVs (world-metre projected
    by WorldUvProjector at runtime) are kept as a second layer but Unity reads the first, which is why
    the bake layer is moved to index 0 before export.
  - Cycles on CPU. A 2048 bake of four shells is minutes, not seconds; the log says where it is.
  - diffuse is baked with only the Color pass, so no lighting is cooked into the albedo. The scene is
    lit at runtime by the preset the owner picks, and a shadow baked into a wall follows the camera
    around wrongly forever.
"""
import bpy, os, sys, time

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
hi_path = os.path.abspath(argv[0] if len(argv) > 0 else "Assets/Models/WhiteHouse.glb")
lo_path = os.path.abspath(argv[1] if len(argv) > 1 else "Assets/Models/WhiteHouse_LOD2.glb")
tex_dir = os.path.abspath(argv[2] if len(argv) > 2 else "Assets/Textures/Building")
size = int(argv[3]) if len(argv) > 3 else 2048
os.makedirs(tex_dir, exist_ok=True)

log_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "whitehouse_facade_bake.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


# The building shells, by the object-name prefix whitehouse_lods.py cuts them under. Trees, cars and
# props are not baked: they are small on screen, and a tree's relief is its leaves, not a surface.
SHELLS = ("Residence", "Wings", "SouthPortico", "NorthPortico")
CAGE_EXTRUSION = 0.25
MAX_RAY = 1.0


def select_only(objs, active):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active


def bake_uv(low):
    """A non-overlapping UV set for the bake, as the mesh's first layer."""
    select_only([low], low)
    uv = low.data.uv_layers.new(name="Bake")
    low.data.uv_layers.active = uv
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.003, correct_aspect=True)
    bpy.ops.object.mode_set(mode="OBJECT")
    # Unity's importer takes the first layer as UV0; move_uv_first puts this one there.
    return uv


def move_uv_first(mesh, name):
    """Blender has no UV reorder op; rebuild the list with `name` first, others in order."""
    layers = mesh.uv_layers
    if list(layers)[0].name == name:
        return
    data = {l.name: [(d.uv[0], d.uv[1]) for d in l.data] for l in layers}
    order = [name] + [n for n in data if n != name]
    while len(layers) > 0:
        layers.remove(layers[0])
    for n in order:
        l = layers.new(name=n)
        for i, (u, v) in enumerate(data[n]):
            l.data[i].uv = (u, v)
    layers.active_index = 0


def bake_target(low, image):
    """A material on the low mesh whose active node is the bake image; Cycles writes into it."""
    mat = bpy.data.materials.new(f"{low.name}_bake")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = image
    nodes.active = tex
    low.data.materials.clear()
    low.data.materials.append(mat)
    return mat


def bake(kind, low, highs, image, colorspace):
    image.colorspace_settings.name = colorspace
    select_only(highs + [low], low)
    scene = bpy.context.scene
    scene.render.bake.use_selected_to_active = True
    scene.render.bake.cage_extrusion = CAGE_EXTRUSION
    scene.render.bake.max_ray_distance = MAX_RAY
    scene.render.bake.margin = 8
    if kind == "DIFFUSE":
        scene.render.bake.use_pass_direct = False
        scene.render.bake.use_pass_indirect = False
        scene.render.bake.use_pass_color = True
    bpy.ops.object.bake(type=kind, use_selected_to_active=True)


t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.device = "CPU"
scene.cycles.samples = 16      # relief and albedo, not lighting; a handful of samples is enough
scene.render.bake.normal_space = "TANGENT"

say(f"importing high {hi_path}")
bpy.ops.import_scene.gltf(filepath=hi_path)
high_objs = {o.name: o for o in bpy.data.objects if o.type == "MESH"}
for o in high_objs.values():
    o.name = "HI_" + o.name     # keep the two files' names apart; the low keeps the real name
say(f"imported {len(high_objs)} high meshes at {time.time() - t0:.0f} s")

say(f"importing low {lo_path}")
bpy.ops.import_scene.gltf(filepath=lo_path)
low_objs = {o.name: o for o in bpy.data.objects if o.type == "MESH" and not o.name.startswith("HI_")}
say(f"imported {len(low_objs)} low meshes at {time.time() - t0:.0f} s")

baked = []
for shell in SHELLS:
    lows = [o for n, o in low_objs.items() if n.startswith(shell)]
    highs = [o for n, o in high_objs.items() if n.startswith("HI_" + shell)]
    if not lows or not highs:
        say(f"{shell}: no {'low' if not lows else 'high'} mesh with that prefix; skipped")
        continue
    for low in lows:
        part = low.name
        say(f"{part}: baking against {len(highs)} high parts ...")
        bake_uv(low)
        move_uv_first(low.data, "Bake")

        n_img = bpy.data.images.new(f"{part}_bakeN", size, size, alpha=False, float_buffer=False)
        d_img = bpy.data.images.new(f"{part}_bakeD", size, size, alpha=False, float_buffer=False)
        n_img.generated_color = (0.5, 0.5, 1.0, 1.0)

        mat = bake_target(low, n_img)
        bake("NORMAL", low, highs, n_img, "Non-Color")
        n_path = os.path.join(tex_dir, f"{part}_bakeN.png")
        n_img.filepath_raw = n_path
        n_img.file_format = "PNG"
        n_img.save()

        mat.node_tree.nodes.active.image = d_img
        bake("DIFFUSE", low, highs, d_img, "sRGB")
        d_path = os.path.join(tex_dir, f"{part}_bakeD.png")
        d_img.filepath_raw = d_path
        d_img.file_format = "PNG"
        d_img.save()

        # Put the original material name back so BuildingLodBuilder's by-name lookup still works for
        # every part that has no bake, and so the baked part is still recognisable in the inspector.
        baked.append(part)
        say(f"{part}: wrote {os.path.basename(n_path)}, {os.path.basename(d_path)} at {time.time() - t0:.0f} s")

# The high meshes are not part of the file being written.
for o in list(high_objs.values()):
    bpy.data.objects.remove(o, do_unlink=True)

bpy.ops.export_scene.gltf(
    filepath=lo_path,
    export_format="GLB",
    export_apply=True,
    export_image_format="NONE",
    export_materials="EXPORT",
    export_texcoords=True,
    export_normals=True,
    export_tangents=True,       # a tangent-space normal map needs the tangents the bake was made with
    export_yup=True,
    use_selection=False,
    export_animations=False,
    export_skins=False,
    export_morph=False,
    export_lights=False,
    export_cameras=False,
    export_extras=False,
)
say(f"rewrote {lo_path} with the Bake UV first ({os.path.getsize(lo_path) / 1e6:.1f} MB)")
say(f"baked {len(baked)} parts: {', '.join(baked)}")
say(f"done in {time.time() - t0:.0f} s. Rebuild the rooftop scenes so BuildingLodBuilder assigns the baked materials, then re-run the sweep.")
log.close()
