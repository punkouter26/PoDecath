"""
Before/after stills of an exported model, so a change to a glb can be judged by eye and not by numbers.

    blender-launcher --background --python training/tools/render_preview.py -- <model.glb|fbx> <out_prefix> building|character [--ao]

Writes <out_prefix>_<shot>.png. Same cameras, sun and sky every time for a given model, so two runs (old
file, new file) line up pixel for pixel. The sun is low and to the side on purpose: raking light is what
shows shading and normal problems. Eevee, 1280x720.
"""
import bpy, math, os, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src, prefix, mode = os.path.abspath(argv[0]), os.path.abspath(argv[1]), argv[2]
show_ao = "--ao" in argv

bpy.ops.wm.read_factory_settings(use_empty=True)
if src.lower().endswith(".fbx"):
    bpy.ops.import_scene.fbx(filepath=src)
else:
    bpy.ops.import_scene.gltf(filepath=src)

if show_ao:
    # Blender's glTF importer keeps the occlusion map on a side node that does not render. Multiply it into
    # base colour so the stills show roughly what an engine does with it.
    for m in bpy.data.materials:
        nt = m.node_tree if m.use_nodes else None
        bsdf = nt and next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
        occ = bsdf and next((l.from_node for l in nt.links if l.to_socket.name == "Occlusion" and l.from_node.type == "SEPARATE_COLOR"), None)
        occ = occ and next((l.from_node for l in nt.links if l.to_node == occ and l.from_node.type == "TEX_IMAGE"), None)
        occ = occ or (nt and next((l.from_node for l in nt.links if l.to_socket.name == "Occlusion" and l.from_node.type == "TEX_IMAGE"), None))
        if not occ:
            continue
        base = next((l for l in nt.links if l.to_socket == bsdf.inputs["Base Color"]), None)
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type, mix.blend_type = "RGBA", "MULTIPLY"
        mix.inputs[0].default_value = 1.0
        if base:
            nt.links.new(base.from_socket, mix.inputs[6])
        else:
            mix.inputs[6].default_value = bsdf.inputs["Base Color"].default_value
        nt.links.new(occ.outputs["Color"], mix.inputs[7])
        nt.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
        print("occlusion shown on", m.name)
sc = bpy.context.scene
sc.render.engine = "BLENDER_EEVEE"
sc.render.resolution_x, sc.render.resolution_y = 1280, 720
sc.eevee.taa_render_samples = 32
sc.view_settings.view_transform = "AgX"

world = bpy.data.worlds.new("Sky")
sc.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.65, 0.8, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.6

sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
sun.data.energy = 4.0
sun.data.angle = math.radians(1.0)
sc.collection.objects.link(sun)


def bbox(objs):
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def look(cam, eye, target):
    cam.location = eye
    cam.rotation_euler = (target - eye).to_track_quat("-Z", "Y").to_euler()


cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
sc.collection.objects.link(cam)
sc.camera = cam
meshes = [o for o in sc.objects if o.type == "MESH" and not o.name.startswith("Icosphere")]
shots = []

if mode == "building":
    res = [o for o in meshes if "Residence" in o.name]
    south = [o for o in meshes if "SouthPortico" in o.name]
    wings = [o for o in meshes if "Wings" in o.name]
    rlo, rhi = bbox(res)
    slo, shi = bbox(south)
    rc, scn = (rlo + rhi) / 2, (slo + shi) / 2
    out = Vector((scn.x - rc.x, scn.y - rc.y, 0)).normalized()      # the way the south front faces
    side = Vector((-out.y, out.x, 0))
    sun.rotation_euler = (out * -1 + side * 1.2 + Vector((0, 0, -0.45))).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = 35
    shots.append(("facade", scn + out * 75 + side * 25 + Vector((0, 0, 8)), rc + Vector((0, 0, 8))))
    shots.append(("portico", scn + out * 22 + side * 14 + Vector((0, 0, 4)), scn + Vector((0, 0, 7))))
    wlo, whi = bbox(wings)
    corner = Vector((whi.x, (wlo.y + whi.y) / 2, wlo.z)) if abs(whi.x - rc.x) > abs(whi.y - rc.y) else Vector(((wlo.x + whi.x) / 2, whi.y, wlo.z))
    shots.append(("wing_end", corner + out * 18 + (corner - rc).normalized() * 10 + Vector((0, 0, 6)), corner + Vector((0, 0, 5)) - (corner - rc).normalized() * 12))
else:
    lo, hi = bbox(meshes)
    c = (lo + hi) / 2
    h = hi.z - lo.z
    fwd = Vector((0, -1, 0))
    sun.rotation_euler = (Vector((0.7, 0.9, -0.6))).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = 50
    shots.append(("front", c + fwd * (h * 2.2) + Vector((0, 0, h * 0.05)), c))
    shots.append(("three_quarter", c + (fwd + Vector((0.9, 0, 0))).normalized() * (h * 2.2) + Vector((0, 0, h * 0.1)), c))
    shots.append(("face", Vector((c.x, c.y, lo.z + h * 0.88)) + fwd * (h * 0.55), Vector((c.x, c.y, lo.z + h * 0.88))))

for name, eye, target in shots:
    look(cam, eye, target)
    sc.render.filepath = f"{prefix}_{name}.png"
    bpy.ops.render.render(write_still=True)
    print("wrote", sc.render.filepath)
