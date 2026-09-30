"""
Blender half of "rebuild a Unity scene in Blender": assembles a new .blend from what
training/tools/unity_scene_export.cs wrote. Never opens or saves the owner's White House .blend.

    blender-launcher --background --python training/tools/unity_scene_to_blend.py -- Blender/source/RooftopRace_scene.json Blender/RooftopRace.blend

Collections:
  Building   - every model the scene places by reference (the White House), imported from its own glb at
               full quality and put where Unity has it. Its LOD1/LOD2 copies are not brought over.
  Generated  - the scene builders' geometry exported from Unity by glTFast (track, skyline, treeline).
  Lighting   - the sun and cameras: Unity's Main Camera, a Track Overview and a Grounds Wide camera.
World: the scene's own HDR panorama, turned to face the way Unity shows it, lighting at Unity's ambient
intensity. Unity's linear fog is a compositor step on the mist pass (renders show it, the viewport does
not). Textures are packed into the .blend so it stands alone.
Writes a half-size preview PNG per camera next to the .blend as a check. Log: training/logs/unity_scene_to_blend.log.
"""
import bpy, json, math, os, sys, time
from mathutils import Vector, Quaternion

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
manifest_path, blend_path = os.path.abspath(argv[0]), os.path.abspath(argv[1])
project = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
log_path = os.path.join(project, "training", "logs", "unity_scene_to_blend.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


man = json.load(open(manifest_path))
scene_name = os.path.splitext(os.path.basename(man["scene"]))[0]
t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.name = scene_name
sc.unit_settings.system = "METRIC"
sc.unit_settings.scale_length = 1.0


def collection(name):
    c = bpy.data.collections.new(name)
    sc.collection.children.link(c)
    return c


def import_into(coll, path):
    """Import a glb and move everything it created into coll. Returns the new objects."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        coll.objects.link(o)
    return new


def unity_to_blender(pos, rot, scale):
    """Unity (left-handed, Y up) -> glTF (right-handed, Y up, x negated) -> Blender (Z up)."""
    x, y, z = pos
    loc = Vector((-x, -z, y))
    qx, qy, qz, qw = rot
    q = Quaternion((qw, qx, qz, -qy))   # glTF (qx, -qy, -qz, qw), then Y-up -> Z-up
    sx, sy, sz = scale
    return loc, q, Vector((sx, sz, sy))


# --- Building: placed models ---------------------------------------------------------------------
building = collection("Building")
holders = {}
for p in man["placed"]:
    model = os.path.join(project, p["model"])
    new = import_into(building, model)
    holder = bpy.data.objects.new(p["name"], None)
    building.objects.link(holder)
    loc, q, s = unity_to_blender(p["position"], p["rotation"], p["scale"])
    holder.location, holder.rotation_mode, holder.rotation_quaternion, holder.scale = loc, "QUATERNION", q, s
    for o in new:
        if o.parent is None:
            o.parent = holder
    holders[p["model"]] = (holder, new)
    say(f"Building: {p['name']} <- {p['model']}: {len(new)} objects")

# --- Generated geometry, sun and camera ----------------------------------------------------------
generated = collection("Generated")
glb = os.path.join(os.path.dirname(manifest_path), f"{scene_name}_generated.glb")
new = import_into(generated, glb)
lighting = collection("Lighting")
for o in new:
    if o.type in ("LIGHT", "CAMERA"):
        generated.objects.unlink(o)
        lighting.objects.link(o)
say(f"Generated: {glb}: {len(new)} objects, "
    f"{sum(1 for o in new if o.type == 'MESH')} meshes, {len({m for o in new if o.type == 'MESH' for m in o.data.materials})} materials")

# Sun: Unity multiplies albedo by intensity; Blender divides diffuse by pi, so strength = pi x intensity.
sun = next((o for o in lighting.objects if o.type == "LIGHT" and o.data.type == "SUN"), None)
if sun and man.get("sun"):
    sun.name = sun.data.name = "Sun"
    sun.data.energy = math.pi * man["sun"]["intensity"]
    sun.data.color = man["sun"]["color"]
    sun.data.angle = math.radians(0.53)
    say(f"Sun: strength {sun.data.energy:.2f} (Unity {man['sun']['intensity']}), colour {man['sun']['color']}")
unity_cam = next((o for o in lighting.objects if o.type == "CAMERA"), None)
if unity_cam:
    unity_cam.name = "Unity Main Camera"
    unity_cam.data.clip_end = 3000

# --- World -------------------------------------------------------------------------------------
env = man["environment"]
world = bpy.data.worlds.new(f"{scene_name} Sky")
sc.world = world
world.use_nodes = True
nt = world.node_tree
nt.nodes.clear()
out = nt.nodes.new("ShaderNodeOutputWorld")
sky_path = os.path.join(project, env["skyTexture"]) if env.get("skyTexture") else ""
if sky_path and os.path.exists(sky_path):
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    # Unity's panoramic skybox puts the image centre on Unity +X; Blender's on its +X, which is Unity -X.
    # Half a turn lines them up; Unity's _Rotation turns the sky the other way round the up axis.
    mapping.inputs["Rotation"].default_value[2] = math.radians(180 - env.get("skyRotation", 0))
    tex = nt.nodes.new("ShaderNodeTexEnvironment")
    tex.image = bpy.data.images.load(sky_path)
    nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
    nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
    # The camera sees the sky at its exposure; everything it lights gets Unity's ambient intensity on top.
    bg_cam = nt.nodes.new("ShaderNodeBackground")
    bg_cam.inputs["Strength"].default_value = env.get("skyExposure", 1)
    bg_lit = nt.nodes.new("ShaderNodeBackground")
    bg_lit.inputs["Strength"].default_value = env.get("skyExposure", 1) * env.get("ambientIntensity", 1)
    nt.links.new(tex.outputs["Color"], bg_cam.inputs["Color"])
    nt.links.new(tex.outputs["Color"], bg_lit.inputs["Color"])
    path = nt.nodes.new("ShaderNodeLightPath")
    mix = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(path.outputs["Is Camera Ray"], mix.inputs["Fac"])
    nt.links.new(bg_lit.outputs["Background"], mix.inputs[1])
    nt.links.new(bg_cam.outputs["Background"], mix.inputs[2])
    nt.links.new(mix.outputs["Shader"], out.inputs["Surface"])
    say(f"World: {env['skyTexture']}, exposure {env.get('skyExposure')}, ambient x{env.get('ambientIntensity')}")
def add_fog(env):
    """Unity's distance fog as a compositor step on the mist pass. A world volume is not an option: Eevee
    treats it as infinite and it swallows the sun and the sky. The mist pass is 0 at fogStart and 1 at
    fogEnd, linear, which is Unity's linear fog exactly; the viewport does not show it, renders do."""
    ms = sc.world.mist_settings
    if env.get("fogMode") == "Linear":
        ms.start, ms.depth, ms.falloff = env["fogStart"], env["fogEnd"] - env["fogStart"], "LINEAR"
    else:
        ms.start, ms.depth, ms.falloff = 0, 3 / max(env["fogDensity"], 1e-6), "QUADRATIC"
    bpy.context.view_layer.use_pass_mist = True
    if hasattr(sc, "compositing_node_group"):                     # Blender 5.x: compositor is a node group
        tree = bpy.data.node_groups.new(f"{scene_name} Fog", "CompositorNodeTree")
        sc.compositing_node_group = tree
        tree.interface.new_socket("Image", in_out="OUTPUT", socket_type="NodeSocketColor")
        out_node = tree.nodes.new("NodeGroupOutput")
        out_sock = out_node.inputs[0]
    else:                                                          # 4.x
        sc.use_nodes = True
        tree = sc.node_tree
        tree.nodes.clear()
        out_node = tree.nodes.new("CompositorNodeComposite")
        out_sock = out_node.inputs["Image"]
    rl = tree.nodes.new("CompositorNodeRLayers")
    mix = tree.nodes.new("ShaderNodeMix") if hasattr(sc, "compositing_node_group") else tree.nodes.new("CompositorNodeMixRGB")
    if mix.bl_idname == "ShaderNodeMix":
        mix.data_type = "RGBA"
        fac, a, b, res = mix.inputs[0], mix.inputs[6], mix.inputs[7], mix.outputs[2]
    else:
        fac, a, b, res = mix.inputs[0], mix.inputs[1], mix.inputs[2], mix.outputs[0]
    b.default_value = (*env["fogColor"], 1)
    # Unity never fogs the skybox, and the sky's mist value is 1, so the fog is masked to geometry: the
    # background's depth is effectively infinite.
    bpy.context.view_layer.use_pass_z = True
    new5 = hasattr(sc, "compositing_node_group")
    solid = tree.nodes.new("ShaderNodeMath" if new5 else "CompositorNodeMath")
    solid.operation = "LESS_THAN"
    solid.inputs[1].default_value = 1e5
    masked = tree.nodes.new("ShaderNodeMath" if new5 else "CompositorNodeMath")
    masked.operation = "MULTIPLY"
    tree.links.new(rl.outputs["Depth"], solid.inputs[0])
    tree.links.new(rl.outputs["Mist"], masked.inputs[0])
    tree.links.new(solid.outputs[0], masked.inputs[1])
    tree.links.new(masked.outputs[0], fac)
    tree.links.new(rl.outputs["Image"], a)
    tree.links.new(res, out_sock)
    say(f"Fog: {env['fogMode']} {ms.start:.0f} m -> {ms.start + ms.depth:.0f} m as mist in the compositor, colour {env['fogColor']}")

if env.get("fog"):
    add_fog(env)


def add_grading(g):
    """The global Volume's grading: tonemapper, exposure, contrast, saturation. Bloom and vignette are not
    reproduced (logged), since Eevee has no bloom and a vignette belongs to the camera shot, not the scene."""
    vs = sc.view_settings
    want = {"Neutral": ["Khronos PBR Neutral", "Standard"], "ACES": ["ACES 2.0", "ACES 1.3", "Filmic", "AgX"],
            "None": ["Standard"]}.get(g.get("tonemapping"), ["AgX"])
    for name in want:
        try:
            vs.view_transform = name
            break
        except TypeError:
            continue
    vs.look = "None"
    vs.exposure = g.get("postExposure", 0)
    tree = getattr(sc, "compositing_node_group", None) if hasattr(sc, "compositing_node_group") else (sc.node_tree if sc.use_nodes else None)
    out_node = tree and next((n for n in tree.nodes if n.bl_idname in ("NodeGroupOutput", "CompositorNodeComposite")), None)
    applied = []
    if out_node and out_node.inputs[0].links:
        src = out_node.inputs[0].links[0].from_socket
        try:
            if g.get("contrast"):
                bc = tree.nodes.new("CompositorNodeBrightContrast")
                bc.inputs["Contrast"].default_value = g["contrast"]
                tree.links.new(src, bc.inputs["Image"])
                src = bc.outputs["Image"]
                applied.append(f"contrast {g['contrast']:+g}")
            if g.get("saturation"):
                hs = tree.nodes.new("CompositorNodeHueSat")
                hs.inputs["Saturation"].default_value = 1 + g["saturation"] / 100
                tree.links.new(src, hs.inputs["Image"])
                src = hs.outputs["Image"]
                applied.append(f"saturation {g['saturation']:+g}")
        except (RuntimeError, KeyError) as ex:
            applied.append(f"contrast/saturation skipped ({ex})")
        tree.links.new(src, out_node.inputs[0])
    say(f"Grading from {g.get('profile')}: {g.get('tonemapping')} tonemapping -> {vs.view_transform}, exposure {vs.exposure:+g}, "
        f"{', '.join(applied) or 'no compositor adjustments'}; not reproduced: bloom {g.get('bloom')}, vignette {g.get('vignette')}")


if man.get("grading"):
    add_grading(man["grading"])


# --- Cameras over the track and the grounds -----------------------------------------------------
def bounds(objs):
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
    return (Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))),
            Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))))


def add_camera(name, target, eye, lens):
    cam = bpy.data.objects.new(name, bpy.data.cameras.new(name))
    lighting.objects.link(cam)
    cam.data.lens, cam.data.clip_end = lens, 5000
    cam.location = eye
    cam.rotation_euler = (target - eye).to_track_quat("-Z", "Y").to_euler()
    return cam


def under(o, name):
    while o is not None:
        if o.name == name:
            return True
        o = o.parent
    return False


track = [o for o in generated.objects if o.type == "MESH" and under(o, "KartTrack")]
lo, hi = bounds(track)
centre, span = (lo + hi) / 2, max(hi.x - lo.x, hi.y - lo.y)
overview = add_camera("Track Overview", centre, centre + Vector((span * 0.9, -span * 1.6, span * 0.8)), 35)
# Frame the White House itself, not the lawn or a city model placed around it.
main_model = next((k for k in holders if "WhiteHouse" in k), next(iter(holders), None))
framed = [o for o in holders[main_model][1] if o.type == "MESH" and not o.name.startswith("Grounds_Turf")] if main_model else []
framed = framed or [o for o in building.objects if o.type == "MESH"]
b_lo, b_hi = bounds(framed)
b_c, b_span = (b_lo + b_hi) / 2, max(b_hi.x - b_lo.x, b_hi.y - b_lo.y)
add_camera("Grounds Wide", b_c, b_c + Vector((b_span * 0.35, -b_span * 0.75, b_span * 0.3)), 28)
sc.camera = overview
say(f"Track on the roof: {tuple(round(v, 1) for v in lo)} .. {tuple(round(v, 1) for v in hi)}; cameras Track Overview, Grounds Wide, Unity Main Camera")

# --- Render settings ----------------------------------------------------------------------------
sc.render.engine = "BLENDER_EEVEE"
sc.render.resolution_x, sc.render.resolution_y = 1920, 1080
sc.view_settings.view_transform = "AgX"
sc.eevee.taa_render_samples = 64
if hasattr(sc.eevee, "use_raytracing"):
    sc.eevee.use_raytracing = True
if hasattr(sc.eevee, "use_shadows"):
    sc.eevee.use_shadows = True

# --- Save and check -----------------------------------------------------------------------------
os.makedirs(os.path.dirname(blend_path), exist_ok=True)
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)
say(f"Saved {blend_path} ({os.path.getsize(blend_path) / 1e6:.0f} MB) in {time.time() - t0:.0f}s")
sc.render.resolution_percentage = 50
for cam in [o for o in lighting.objects if o.type == "CAMERA"]:
    sc.camera = cam
    preview = os.path.splitext(blend_path)[0] + "_" + cam.name.lower().replace(" ", "_") + ".png"
    sc.render.filepath = preview
    bpy.ops.render.render(write_still=True)
    say(f"Preview {preview}")
sc.camera = overview
say("DONE")
