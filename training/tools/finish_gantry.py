"""
The finish-line gantry: two steel legs, a box truss over the track, a timing clock on the truss and a
16:9 video screen above it. Built from nothing in an empty Blender scene (no .blend is opened or saved)
and exported for Unity.

    blender-launcher --background --python training/tools/finish_gantry.py -- [out.glb] [--max-tris 6000]

Default out = Assets/Models/FinishGantry.glb. Log at training/logs/finish_gantry.log.

Axes and origin (real-world metres, applied transforms, one root node "FinishGantry"):
  - the origin is on the ground (y = 0) on the track's centre line, under the middle of the beam;
  - glTF +Y is up. The beam spans glTF X (across the track). The track runs along glTF Z;
  - the "front" faces point along glTF +Z, which glTFast imports as Unity +Z, i.e. transform.forward.
    Turn the gantry so forward points back down the track at the runners coming in. The screen and the
    clock have a second face on the back, so they read from both directions.
  (In Blender, before the exporter's +Y-up conversion: X across, Y along the track with the front facing
  -Y, Z up.)

Dimensions, from the track it stands over (5.3 m deck, five 1.08 m lanes):
  - legs 0.25 m square steel, inner faces 4.8 m apart by default (--clear-width): each leg stands on
    one of the 0.25 m barriers along the 5.3 m deck, since outside them there is only air, on
    0.5 m base plates with four anchor bolts;
  - box truss 0.6 m deep x 0.5 m wide, 80 mm chords and 45 mm web, underside at 4.2 m clearance, capped
    by end plates at the legs;
  - timing-clock box 1.9 x 0.6 m around the middle of the truss, a 1.7 x 0.44 m face each side;
  - a 3.2 x 1.8 m (16:9) screen each side of a 3.44 x 2.04 x 0.34 m housing on two posts above the truss,
    top of the housing at 7.04 m.

What Unity gets, by name, because that is how the scene finds it:
  objects   Gantry_Frame (legs, truss, plates), Gantry_Housing (screen housing and clock box),
            Gantry_Screen, Gantry_Clock
  materials Gantry_Steel (painted metal), Gantry_Housing (dark), Gantry_Screen (the two screen faces
            only), Gantry_Clock (the two clock faces only)
Each screen or clock face is one quad whose UVs cover 0..1 exactly, upright and not mirrored when looked
at from its own side (u runs to the viewer's right, v up), so a RenderTexture drops straight on. The
script checks that and refuses to export if it is wrong.

Shading: the legs, plates, chords and housings are bevelled (8 mm steel, 12 mm housing, two segments) with
hardened normals, so the flats shade flat and the edges catch the light; the thin truss web is left
square and flat-shaded; the display faces are flat quads. Budget: the export must stay under --max-tris.
"""
import argparse, bmesh, bpy, math, os, sys, time
from mathutils import Matrix, Vector

project = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser(prog="finish_gantry.py")
ap.add_argument("out", nargs="?", default=os.path.join(project, "Assets", "Models", "FinishGantry.glb"))
ap.add_argument("--max-tris", type=int, default=6000)
ap.add_argument("--clear-width", type=float, default=4.8,
                help="metres between the legs' inner faces; 4.8 stands each leg on a 0.25 m barrier of the 5.3 m deck")
args = ap.parse_args(argv)
out_path = os.path.abspath(args.out)

log_path = os.path.join(project, "training", "logs", "finish_gantry.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


# ------------------------------------------------------------------------------------------ dimensions
CLEAR_WIDTH = args.clear_width   # between the legs' inner faces
LEG = 0.25                 # square leg section
CLEARANCE = 4.2            # underside of the truss
TRUSS_H, TRUSS_D = 0.6, 0.5
CHORD, WEB = 0.08, 0.045
BAYS = 10
SCREEN_W, SCREEN_H = 3.2, 1.8
BEZEL = 0.12               # housing border around the screen
HOUSING_D = 0.34
HOUSING_GAP = 0.2          # posts between truss top and housing bottom
CLOCK_BOX = (1.9, 0.66, TRUSS_H)
CLOCK_FACE = (1.7, 0.44)
PROUD = 0.003              # display faces sit this far off the housing so they never z-fight

LEG_X = CLEAR_WIDTH / 2 + LEG / 2                  # leg centre line
END_X = CLEAR_WIDTH / 2 + LEG                      # outer face of the legs = ends of the truss
TOP = CLEARANCE + TRUSS_H                          # top of the truss = top of the legs
H_BOTTOM = TOP + HOUSING_GAP
H_W, H_H = SCREEN_W + 2 * BEZEL, SCREEN_H + 2 * BEZEL


def srgb(r, g, b):
    """Design colours are written as sRGB; node sockets want linear."""
    f = lambda c: c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (f(r), f(g), f(b), 1.0)


def material(name, rgb, metallic, roughness):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = srgb(*rgb)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    m.diffuse_color = srgb(*rgb)
    return m


class Part:
    """One mesh object built in a bmesh: boxes, struts between two points, and UV'd quads."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.bw = self.bm.edges.layers.float.new("bevel_weight_edge")
        self.uv = self.bm.loops.layers.uv.new("UVMap")

    def box(self, center, size, bevel=True, frame=None):
        m = Matrix.Translation(Vector(center)) @ (frame or Matrix.Identity(4)) @ Matrix.Diagonal((*size, 1.0))
        verts = bmesh.ops.create_cube(self.bm, size=1.0, matrix=m)["verts"]
        faces = {f for v in verts for f in v.link_faces}
        for f in faces:
            f.smooth = bevel
            for loop in f.loops:     # box-mapped UVs in metres; nothing samples them, glTF wants a set
                co = loop.vert.co
                n = f.normal
                a = max(range(3), key=lambda i: abs(n[i]))
                u, v = [co[i] for i in range(3) if i != a]
                loop[self.uv].uv = (u, v)
        for e in {e for v in verts for e in v.link_edges}:
            e[self.bw] = 1.0 if bevel else 0.0

    def strut(self, p0, p1, w, bevel=False):
        """A square tube from p0 to p1, its faces aligned with the plane it lies in."""
        p0, p1 = Vector(p0), Vector(p1)
        z = (p1 - p0).normalized()
        ref = Vector((0, 0, 1)) if abs(z.z) < 0.9 else Vector((1, 0, 0))
        x = ref.cross(z).normalized()
        y = z.cross(x)
        frame = Matrix((x, y, z)).transposed().to_4x4()
        self.box((p0 + p1) / 2, (w, w, (p1 - p0).length), bevel, frame)

    def quad(self, corners, uvs):
        """corners counter-clockwise seen from the side the face shows to."""
        vs = [self.bm.verts.new(Vector(c)) for c in corners]
        f = self.bm.faces.new(vs)
        f.smooth = False
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        return f

    def build(self, mat, parent, bevel_width=0.0):
        mesh = bpy.data.meshes.new(self.name)
        self.bm.normal_update()
        self.bm.to_mesh(mesh)
        self.bm.free()
        mesh.materials.append(mat)
        obj = bpy.data.objects.new(self.name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = parent
        if bevel_width > 0:
            mod = obj.modifiers.new("bevel", "BEVEL")
            mod.limit_method = "WEIGHT"
            mod.width = bevel_width
            mod.segments = 2
            mod.use_clamp_overlap = True
            mod.harden_normals = True
            bpy.ops.object.select_all(action="DESELECT")
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            bpy.ops.object.modifier_apply(modifier=mod.name)
        return obj


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def display(part, width, height, cz, face_y, flip):
    """A face of width x height centred at (0, face_y, cz). flip=False faces -Y (the front), True +Y.
    u runs to the right of someone looking at the face, v up."""
    x0, x1, z0, z1 = -width / 2, width / 2, cz - height / 2, cz + height / 2
    if not flip:    # looking along +Y from the front, right is +X
        part.quad([(x0, face_y, z0), (x1, face_y, z0), (x1, face_y, z1), (x0, face_y, z1)],
                  [(0, 0), (1, 0), (1, 1), (0, 1)])
    else:           # looking along -Y from the back, right is -X
        part.quad([(x1, face_y, z0), (x0, face_y, z0), (x0, face_y, z1), (x1, face_y, z1)],
                  [(0, 0), (1, 0), (1, 1), (0, 1)])


def check_display(obj):
    """Every face: UVs span 0..1 exactly, u grows to the viewer's right, v grows up. Returns problems."""
    bad = []
    uvl = obj.data.uv_layers[0].data
    for p in obj.data.polygons:
        n = p.normal
        right = (-n).cross(Vector((0, 0, 1)))       # the viewer looks along -n, up is +Z
        cos = [obj.matrix_world @ obj.data.vertices[obj.data.loops[i].vertex_index].co for i in p.loop_indices]
        uvs = [uvl[i].uv.copy() for i in p.loop_indices]
        us, vs = [uv.x for uv in uvs], [uv.y for uv in uvs]
        if min(us) != 0 or max(us) != 1 or min(vs) != 0 or max(vs) != 1:
            bad.append(f"{obj.name} face {p.index}: UV range u {min(us)}..{max(us)} v {min(vs)}..{max(vs)}")
        mr = sum(c.dot(right) for c in cos) / len(cos)
        mz = sum(c.z for c in cos) / len(cos)
        cu = sum((c.dot(right) - mr) * (uv.x - 0.5) for c, uv in zip(cos, uvs))
        cv = sum((c.z - mz) * (uv.y - 0.5) for c, uv in zip(cos, uvs))
        if cu <= 0 or cv <= 0:
            bad.append(f"{obj.name} face {p.index} (normal {tuple(round(a, 2) for a in n)}): "
                       f"{'mirrored' if cu <= 0 else ''} {'upside down' if cv <= 0 else ''}")
    return bad


def worst_flat_normal(obj):
    """Largest angle (deg) between a corner normal and its face normal over the flat faces (every face
    whose shortest edge is wider than a bevel strip), which is where smooth-shading on a bevelled box
    shows as a gradient. Hardened normals keep this ~0; the bevel strips themselves are meant to curve."""
    worst = 0.0
    me = obj.data
    cn = me.corner_normals
    for p in me.polygons:
        ks = p.edge_keys
        if min((me.vertices[a].co - me.vertices[b].co).length for a, b in ks) < 0.03:
            continue
        for i in p.loop_indices:
            worst = max(worst, math.degrees(p.normal.angle(cn[i].vector, 0.0)))
    return worst


# ------------------------------------------------------------------------------------------------ build
t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.unit_settings.system, sc.unit_settings.scale_length = "METRIC", 1.0

steel = material("Gantry_Steel", (0.74, 0.76, 0.78), 0.0, 0.38)        # painted: the paint is dielectric
housing = material("Gantry_Housing", (0.045, 0.047, 0.052), 0.0, 0.55)
screen = material("Gantry_Screen", (0.015, 0.017, 0.022), 0.0, 0.18)   # a live RenderTexture in Unity
clock = material("Gantry_Clock", (0.01, 0.01, 0.01), 0.0, 0.3)

root = bpy.data.objects.new("FinishGantry", None)
sc.collection.objects.link(root)

# Legs, base plates with anchor bolts, caps.
frame = Part("Gantry_Frame")
for s in (-1, 1):
    x = s * LEG_X
    frame.box((x, 0, TOP / 2), (LEG, LEG, TOP))
    frame.box((x, 0, 0.0125), (0.5, 0.5, 0.025))
    frame.box((x, 0, TOP + 0.01), (LEG + 0.04, TRUSS_D + 0.04, 0.02))
    for bx in (-0.18, 0.18):
        for by in (-0.18, 0.18):
            frame.box((x + bx, by, 0.04), (0.036, 0.036, 0.03), bevel=False)
    # end plate closing the truss at the leg
    frame.box((s * (END_X - 0.01), 0, CLEARANCE + TRUSS_H / 2), (0.02, TRUSS_D, TRUSS_H))

# Truss: four chords the full width, Warren web on all four faces.
yc = TRUSS_D / 2 - CHORD / 2
zb, zt = CLEARANCE + CHORD / 2, TOP - CHORD / 2
for y in (-yc, yc):
    for z in (zb, zt):
        frame.box((0, y, z), (2 * END_X, CHORD, CHORD))
xs = [-LEG_X + i * (2 * LEG_X) / BAYS for i in range(BAYS + 1)]
for y in (-yc, yc):                                     # front and back faces
    for x in xs:
        frame.strut((x, y, zb), (x, y, zt), WEB)
    for i in range(BAYS):
        a, b = (zb, zt) if i % 2 == 0 else (zt, zb)
        frame.strut((xs[i], y, a), (xs[i + 1], y, b), WEB)
for z in (zb, zt):                                      # bottom and top faces
    for x in xs:
        frame.strut((x, -yc, z), (x, yc, z), WEB)
    for i in range(BAYS):
        a, b = (-yc, yc) if i % 2 == 0 else (yc, -yc)
        frame.strut((xs[i], a, z), (xs[i + 1], b, z), WEB)

# Posts carrying the screen housing.
for s in (-1, 1):
    frame.box((s * 1.2, 0, TOP + HOUSING_GAP / 2), (0.12, 0.12, HOUSING_GAP))
    frame.box((s * 1.2, 0, TOP + 0.03), (0.24, 0.3, 0.02))

# Housings: the screen box and the clock box around the middle of the truss.
hous = Part("Gantry_Housing")
screen_cz = H_BOTTOM + H_H / 2
hous.box((0, 0, screen_cz), (H_W, HOUSING_D, H_H))
clock_cz = CLEARANCE + TRUSS_H / 2
hous.box((0, 0, clock_cz), CLOCK_BOX)

scr = Part("Gantry_Screen")
display(scr, SCREEN_W, SCREEN_H, screen_cz, -(HOUSING_D / 2 + PROUD), flip=False)
display(scr, SCREEN_W, SCREEN_H, screen_cz, +(HOUSING_D / 2 + PROUD), flip=True)

clk = Part("Gantry_Clock")
display(clk, CLOCK_FACE[0], CLOCK_FACE[1], clock_cz, -(CLOCK_BOX[1] / 2 + PROUD), flip=False)
display(clk, CLOCK_FACE[0], CLOCK_FACE[1], clock_cz, +(CLOCK_BOX[1] / 2 + PROUD), flip=True)

objs = [
    frame.build(steel, root, bevel_width=0.008),
    hous.build(housing, root, bevel_width=0.012),
    scr.build(screen, root),
    clk.build(clock, root),
]

# ------------------------------------------------------------------------------------------------ check
problems = check_display(objs[2]) + check_display(objs[3])
total = 0
for o in objs:
    n = tri_count(o)
    total += n
    lo = Vector([min((v.co[i] for v in o.data.vertices)) for i in range(3)])
    hi = Vector([max((v.co[i] for v in o.data.vertices)) for i in range(3)])
    say(f"{o.name}: {n} triangles, material {o.data.materials[0].name}, "
        f"x {lo.x:.3f}..{hi.x:.3f}  along-track {lo.y:.3f}..{hi.y:.3f}  up {lo.z:.3f}..{hi.z:.3f}, "
        f"worst flat-face normal {worst_flat_normal(o):.1f} deg")
say(f"total {total} triangles (budget {args.max_tris})")
legs_inner = 2 * (LEG_X - LEG / 2)
say(f"clear width between legs {legs_inner:.3f} m, clearance under truss {CLEARANCE:.3f} m, "
    f"screen {SCREEN_W} x {SCREEN_H} m ({SCREEN_W / SCREEN_H:.3f}:1), top {H_BOTTOM + H_H:.3f} m")
if total > args.max_tris:
    problems.append(f"{total} triangles is over the {args.max_tris} budget")
if problems:
    for p in problems:
        say("PROBLEM " + p)
    say("not exported")
    log.close()
    sys.exit(1)
say("screen and clock faces: UVs 0..1, upright, not mirrored from either side")

os.makedirs(os.path.dirname(out_path), exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=out_path,
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
say(f"wrote {out_path} ({os.path.getsize(out_path) / 1e3:.0f} kB) in {time.time() - t0:.1f} s")
log.close()
