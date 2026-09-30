"""
Turn the placeholder city around the White House into something that reads as a city, in Blender, and
export it for Unity. Works on the .blend made by unity_scene_to_blend.py; never touches the owner's .blend.

    blender-launcher --background --python training/tools/improve_surroundings.py -- Blender/RooftopRace.blend Assets/Models/Surroundings.glb

What changes (the Unity SurroundingsBuilder made all three as flat-colour placeholders):
  * Skyline: the 200-odd grey boxes get facades - limestone, brick and glass curtain wall - with windows,
    floor lines and a per-window tone so no two floors repeat exactly; roofs get their own material; about
    half the roofs get a stair/plant bulkhead so the roofline is not a row of flat lids. Windows are sized so
    every face shows whole bays (4 m) and whole floors (3.6 m).
  * Obelisk: marble with the real monument's change of stone a quarter of the way up and faint courses.
  * City ground: a ring of streets and blocks from 410 m out, with a grass strip down the Mall, so blocks past
    the edge of the lawn no longer stand on nothing.
Game-ready pipeline, as the export checklist asks: metric scale 1.0; transforms applied and every origin at
the city centre on the ground; duplicate vertices merged, normals recalculated outward, flat shading; every
material starts as procedural nodes (kept in the .blend as *_Procedural, fake user, to re-bake) and is baked
to Base Colour (sRGB), a channel-packed ORM (R occlusion, G roughness, B metallic) and a tangent-space normal
map; UV0 tiles in metres, UV1 is a non-overlapping lightmap layout with 4-texel padding at 1024; the three
objects sit under one root in their own collection; orphan data is purged; glb with tangents, +Y up.
Log: training/logs/improve_surroundings.log. Writes <blend>_city_drone.png as a check.
"""
import bpy, bmesh, math, os, random, sys, time
import numpy as np
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
blend_path, glb_path = os.path.abspath(argv[0]), os.path.abspath(argv[1])
project = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
log_path = os.path.join(project, "training", "logs", "improve_surroundings.log")
os.makedirs(os.path.dirname(log_path), exist_ok=True)
log = open(log_path, "w")


def say(msg):
    log.write(msg + "\n")
    log.flush()
    print(msg)


t0 = time.time()
bpy.ops.wm.open_mainfile(filepath=blend_path)
sc = bpy.context.scene
sc.unit_settings.system, sc.unit_settings.scale_length = "METRIC", 1.0
rng = random.Random(1600)


def srgb(r, g, b):
    """Design colours are written as sRGB; node sockets want linear."""
    f = lambda c: c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (f(r), f(g), f(b), 1.0)


# ================================================================ procedural materials -> baked PBR
class G:
    """Small helper for writing node graphs as expressions."""
    def __init__(self, mat):
        mat.use_nodes = True
        self.nt = mat.node_tree
        self.nt.nodes.clear()
        tc = self.node("ShaderNodeTexCoord")
        sep = self.node("ShaderNodeSeparateXYZ")
        self.link(tc.outputs["UV"], sep.inputs[0])
        self.uv, self.u, self.v = tc.outputs["UV"], sep.outputs[0], sep.outputs[1]

    def node(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, val in props.items():
            setattr(n, k, val)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def put(self, sock, val):
        if isinstance(val, bpy.types.NodeSocket):
            self.link(val, sock)
        else:
            sock.default_value = val

    def m(self, op, a, b=None, clamp=False):
        n = self.node("ShaderNodeMath", operation=op, use_clamp=clamp)
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        return n.outputs[0]

    def mixc(self, fac, a, b):
        n = self.node("ShaderNodeMix", data_type="RGBA")
        self.put(n.inputs[0], fac)
        self.put(n.inputs[6], a)
        self.put(n.inputs[7], b)
        return n.outputs[2]

    def mixf(self, fac, a, b):
        n = self.node("ShaderNodeMix", data_type="FLOAT")
        self.put(n.inputs[0], fac)
        self.put(n.inputs[2], a)
        self.put(n.inputs[3], b)
        return n.outputs[0]

    def noise(self, scale, detail=4.0, vec=None):
        n = self.node("ShaderNodeTexNoise")
        n.inputs["Scale"].default_value = scale
        n.inputs["Detail"].default_value = detail
        self.link(vec if vec is not None else self.uv, n.inputs["Vector"])
        return n.outputs["Fac"]

    def white(self, x, y, z):
        c = self.node("ShaderNodeCombineXYZ")
        self.put(c.inputs[0], x)
        self.put(c.inputs[1], y)
        self.put(c.inputs[2], z)
        n = self.node("ShaderNodeTexWhiteNoise", noise_dimensions="3D")
        self.link(c.outputs[0], n.inputs["Vector"])
        return n.outputs["Value"]

    def between(self, x, lo, hi):
        return self.m("MULTIPLY", self.m("GREATER_THAN", x, lo), self.m("LESS_THAN", x, hi))


def facade(p):
    """Bays x floors of windows in one tile. Returns the graph builder for bake()."""
    def build(g):
        bu, bv = g.m("MULTIPLY", g.u, p["bays"]), g.m("MULTIPLY", g.v, p["floors"])
        cu, cv, iu, iv = g.m("FRACT", bu), g.m("FRACT", bv), g.m("FLOOR", bu), g.m("FLOOR", bv)
        (x0, x1), (y0, y1) = p["win_x"], p["win_y"]
        win = g.m("MULTIPLY", g.between(cu, x0, x1), g.between(cv, y0, y1))
        edge = g.m("MINIMUM", g.m("MINIMUM", g.m("SUBTRACT", cu, x0), g.m("SUBTRACT", x1, cu)),
                   g.m("MINIMUM", g.m("SUBTRACT", cv, y0), g.m("SUBTRACT", y1, cv)))
        r = g.white(iu, iv, p["seed"])
        glass = g.mixc(r, srgb(*p["glass_a"]), srgb(*p["glass_b"]))
        glass = g.mixc(g.m("GREATER_THAN", r, 0.86), glass, srgb(*p["curtain"]))   # drawn blinds, some rooms lit
        wall = g.mixc(g.m("MULTIPLY", g.noise(p["grain"]), 0.9), srgb(*p["wall"]), srgb(*p["wall_dark"]))
        band = g.m("LESS_THAN", cv, p["band"])                                   # floor line / spandrel
        wall = g.mixc(band, wall, srgb(*p["band_colour"]))
        if p.get("lintel"):
            lintel = g.m("MULTIPLY", g.between(cu, x0 - 0.03, x1 + 0.03), g.between(cv, y1, y1 + 0.06))
            wall = g.mixc(lintel, wall, srgb(*p["band_colour"]))
            band = g.m("MAXIMUM", band, lintel)
        base = g.mixc(win, wall, glass)
        rough = g.mixf(win, g.mixf(band, p["wall_rough"], p["band_rough"]), p["glass_rough"])
        metal = g.mixf(win, p["wall_metal"], 0.0)
        height = g.m("ADD", g.m("SUBTRACT", 1.0, win), g.m("MULTIPLY", band, 0.4))
        reveal = g.m("SUBTRACT", 1.0, g.m("MINIMUM", g.m("DIVIDE", edge, 0.06), 1.0))
        ao = g.m("SUBTRACT", 1.0, g.m("MULTIPLY", g.m("MULTIPLY", win, reveal), 0.5))
        return dict(base=base, rough=rough, metal=metal, ao=ao, height=height, bump=0.06)
    return build


FACADES = {
    "Facade_Limestone": dict(bays=4, floors=3, win_x=(0.32, 0.68), win_y=(0.22, 0.80), seed=1.0,
                             wall=(0.80, 0.76, 0.68), wall_dark=(0.70, 0.66, 0.58), grain=38.0,
                             band=0.07, band_colour=(0.86, 0.83, 0.76), glass_a=(0.10, 0.11, 0.12),
                             glass_b=(0.19, 0.21, 0.23), curtain=(0.62, 0.57, 0.48), wall_rough=0.82,
                             band_rough=0.75, glass_rough=0.08, wall_metal=0.0),
    "Facade_Brick": dict(bays=4, floors=3, win_x=(0.28, 0.72), win_y=(0.25, 0.74), seed=2.0, lintel=True,
                         wall=(0.55, 0.30, 0.22), wall_dark=(0.42, 0.22, 0.16), grain=90.0,
                         band=0.035, band_colour=(0.85, 0.82, 0.75), glass_a=(0.09, 0.10, 0.11),
                         glass_b=(0.17, 0.18, 0.20), curtain=(0.66, 0.60, 0.50), wall_rough=0.88,
                         band_rough=0.8, glass_rough=0.08, wall_metal=0.0),
    "Facade_Glass": dict(bays=4, floors=3, win_x=(0.05, 0.95), win_y=(0.10, 0.95), seed=3.0,
                         wall=(0.60, 0.62, 0.65), wall_dark=(0.52, 0.54, 0.57), grain=20.0,
                         band=0.10, band_colour=(0.22, 0.23, 0.25), glass_a=(0.14, 0.19, 0.24),
                         glass_b=(0.24, 0.30, 0.36), curtain=(0.45, 0.47, 0.48), wall_rough=0.4,
                         band_rough=0.5, glass_rough=0.18, wall_metal=0.7),
}


def roof(g):
    n1, n2 = g.noise(60.0, 6.0), g.noise(7.0, 2.0)
    base = g.mixc(g.m("MULTIPLY", n1, 1.0), srgb(0.40, 0.40, 0.39), srgb(0.28, 0.28, 0.27))
    base = g.mixc(g.m("GREATER_THAN", n2, 0.62), base, srgb(0.50, 0.49, 0.46))       # patched membrane
    return dict(base=base, rough=0.93, metal=0.0, ao=g.m("ADD", 0.85, g.m("MULTIPLY", n1, 0.15)), height=n1, bump=0.02)


def marble(g):
    # UV v runs 0..1 over the 169 m; the stone changes at 46 m (0.272). Courses every 2 m.
    lower = g.m("LESS_THAN", g.v, 0.272)
    n = g.noise(25.0, 5.0)
    stone = g.mixc(lower, srgb(0.86, 0.83, 0.77), srgb(0.90, 0.89, 0.86))
    stone = g.mixc(g.m("MULTIPLY", n, 0.35), stone, srgb(0.78, 0.76, 0.72))
    course = g.m("LESS_THAN", g.m("FRACT", g.m("MULTIPLY", g.v, 84.5)), 0.035)
    base = g.mixc(g.m("MULTIPLY", course, 0.5), stone, srgb(0.66, 0.64, 0.60))
    return dict(base=base, rough=0.45, metal=0.0, ao=g.m("SUBTRACT", 1.0, g.m("MULTIPLY", course, 0.3)),
                height=g.m("SUBTRACT", 1.0, course), bump=0.01)


def city_ground(g):
    # One tile is 90 m: three blocks each way with streets between.
    cu, cv = g.m("FRACT", g.m("MULTIPLY", g.u, 3.0)), g.m("FRACT", g.m("MULTIPLY", g.v, 3.0))
    street = g.m("MAXIMUM", g.m("LESS_THAN", cu, 0.14), g.m("LESS_THAN", cv, 0.14))
    n1, n2 = g.noise(9.0, 3.0), g.noise(40.0, 4.0)
    canopy = g.m("GREATER_THAN", n1, 0.56)
    # Seen from above a city is dark: roofs, asphalt and tree canopy, not pavement white.
    block = g.mixc(n2, srgb(0.31, 0.30, 0.29), srgb(0.22, 0.22, 0.21))
    block = g.mixc(canopy, block, srgb(0.16, 0.23, 0.11))
    base = g.mixc(street, block, srgb(0.14, 0.14, 0.15))
    rough = g.mixf(street, g.mixf(canopy, 0.8, 0.95), 0.9)
    height = g.m("MULTIPLY", g.m("SUBTRACT", 1.0, street), g.m("ADD", 0.3, canopy))
    return dict(base=base, rough=rough, metal=0.0, ao=g.m("SUBTRACT", 1.0, g.m("MULTIPLY", street, 0.12)), height=height, bump=0.5)


def mall_grass(colour):
    def build(g):
        n = g.noise(30.0, 6.0)
        base = g.mixc(g.m("MULTIPLY", n, 0.6), srgb(*colour), srgb(*(c * 0.8 for c in colour)))
        return dict(base=base, rough=0.95, metal=0.0, ao=1.0, height=n, bump=0.05)
    return build


# --- bake --------------------------------------------------------------------------------------
bake_plane = None
gltf_out = None


def gltf_output_group():
    global gltf_out
    if gltf_out is None:
        gltf_out = bpy.data.node_groups.get("glTF Material Output") or bpy.data.node_groups.new("glTF Material Output", "ShaderNodeTree")
        if "Occlusion" not in [s.name for s in gltf_out.interface.items_tree]:
            gltf_out.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
    return gltf_out


def image(name, size, colour):
    img = bpy.data.images.new(name, size[0], size[1], alpha=False)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    return img


def bake(name, build, base_size, small_size):
    """Procedural graph -> Base Colour, ORM and normal images -> a plain image-texture material."""
    global bake_plane
    if bake_plane is None:
        bpy.ops.mesh.primitive_plane_add(size=1.0)
        bake_plane = bpy.context.active_object
        bake_plane.name = "_bake_plane"
    src = bpy.data.materials.new(name + "_Procedural")
    src.use_fake_user = True
    g = G(src)
    ch = build(g)
    out = g.node("ShaderNodeOutputMaterial")
    emit = g.node("ShaderNodeEmission")
    bsdf = g.node("ShaderNodeBsdfPrincipled")
    bump = g.node("ShaderNodeBump")
    bump.inputs["Distance"].default_value = ch["bump"]
    g.put(bump.inputs["Height"], ch["height"])
    g.link(bump.outputs["Normal"], bsdf.inputs["Normal"])
    for k in ("base", "rough", "metal"):
        target = {"base": "Base Color", "rough": "Roughness", "metal": "Metallic"}[k]
        g.put(bsdf.inputs[target], ch[k])
    target_node = g.node("ShaderNodeTexImage")
    g.nt.nodes.active = target_node
    bake_plane.data.materials.clear()
    bake_plane.data.materials.append(src)
    for o in bpy.context.view_layer.objects:
        o.select_set(o == bake_plane)
    bpy.context.view_layer.objects.active = bake_plane

    def run(kind, sock, img):
        for l in list(out.inputs["Surface"].links):
            g.nt.links.remove(l)
        if kind == "EMIT":
            for l in list(emit.inputs["Color"].links):
                g.nt.links.remove(l)
            g.put(emit.inputs["Color"], sock if isinstance(sock, bpy.types.NodeSocket) else (sock, sock, sock, 1.0))
            g.link(emit.outputs[0], out.inputs["Surface"])
        else:
            g.link(bsdf.outputs[0], out.inputs["Surface"])
        target_node.image = img
        bpy.ops.object.bake(type=kind, margin=0, use_clear=True, normal_space="TANGENT")
        img.pack()
        return img

    base = run("EMIT", ch["base"], image(name + "_BaseColor", base_size, True))
    layers = []
    for k in ("ao", "rough", "metal"):
        tmp = run("EMIT", ch[k], image(f"_{name}_{k}", small_size, False))
        layers.append(np.array(tmp.pixels[:]).reshape(-1, 4)[:, 0])
        bpy.data.images.remove(tmp)
    orm = image(name + "_ORM", small_size, False)
    orm.pixels = np.stack(layers + [np.ones_like(layers[0])], 1).ravel().tolist()
    orm.pack()
    nrm = run("NORMAL", None, image(name + "_Normal", small_size, False))

    mat = bpy.data.materials.new(name)
    mat.use_backface_culling = True          # closed, outward-facing geometry: single-sided in the glb
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    p = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(p.outputs[0], o.inputs["Surface"])
    tb, to, tn = (nt.nodes.new("ShaderNodeTexImage") for _ in range(3))
    tb.image, to.image, tn.image = base, orm, nrm
    nt.links.new(tb.outputs["Color"], p.inputs["Base Color"])
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(to.outputs["Color"], sep.inputs[0])
    nt.links.new(sep.outputs[1], p.inputs["Roughness"])
    nt.links.new(sep.outputs[2], p.inputs["Metallic"])
    grp = nt.nodes.new("ShaderNodeGroup")
    grp.node_tree = gltf_output_group()
    nt.links.new(sep.outputs[0], grp.inputs["Occlusion"])
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(tn.outputs["Color"], nm.inputs["Color"])
    nt.links.new(nm.outputs["Normal"], p.inputs["Normal"])
    say(f"baked {name}: base {base_size[0]}x{base_size[1]}, ORM + normal {small_size[0]}x{small_size[1]}")
    return mat


# ================================================================ geometry
def find(name):
    o = bpy.data.objects.get(name)
    if o is None:
        raise SystemExit(f"{name} not in {blend_path}; rebuild it with unity_scene_to_blend.py")
    return o


skyline, obelisk = find("Skyline"), find("Obelisk")
lawn = bpy.data.objects.get("Grounds_Turf")


def bake_transform(o):
    """Apply the full world transform into the mesh and detach it: origin at the world origin."""
    mw = o.matrix_world.copy()
    o.parent = None
    o.data = o.data.copy()
    o.data.transform(mw)
    o.matrix_world = Matrix.Identity(4)


def drop_custom_normals(o):
    """The meshes came from Unity with custom normals; after merging and re-winding they no longer belong
    to the faces, and the glb would carry them. Flat shading from the faces themselves is what these are."""
    if not o.data.has_custom_normals:
        return False
    for x in bpy.context.view_layer.objects:
        x.select_set(x == o)
    bpy.context.view_layer.objects.active = o
    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]):
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    return True


for o in (skyline, obelisk):
    bake_transform(o)
    if drop_custom_normals(o):
        say(f"{o.name}: cleared the custom normals it arrived with")

# The city centre, from the obelisk: Unity puts it at centre + (220, 0, 1250), which is (-220, -1250, 0)
# in Blender's axes. Ground level is the obelisk's foot.
ob_pts = [v.co for v in obelisk.data.vertices]
ob_base = Vector((sum(p.x for p in ob_pts) / len(ob_pts), sum(p.y for p in ob_pts) / len(ob_pts), min(p.z for p in ob_pts)))
centre = Vector((ob_base.x + 220.0, ob_base.y + 1250.0, ob_base.z))
sky_pts = np.array([v.co[:] for v in skyline.data.vertices])
say(f"city centre {tuple(round(c, 2) for c in centre)}; skyline vertex mean offset from it "
    f"({sky_pts[:, 0].mean() - centre.x:.0f}, {sky_pts[:, 1].mean() - centre.y:.0f}) m (the open Mall wedge pulls it north)")
lawn_half = 700.0
if lawn:
    lp = [lawn.matrix_world @ Vector(c) for c in lawn.bound_box]
    lawn_half = min(max(p.x for p in lp) - min(p.x for p in lp), max(p.y for p in lp) - min(p.y for p in lp)) / 2
    lawn_top = max(p.z for p in lp)
    say(f"lawn: half-size {lawn_half:.0f} m, top at z {lawn_top:.2f}")


def islands(bm):
    seen, out = set(), []
    for f in bm.faces:
        if f.index in seen:
            continue
        stack, isl = [f], []
        seen.add(f.index)
        while stack:
            cur = stack.pop()
            isl.append(cur)
            for e in cur.edges:
                for nb in e.link_faces:
                    if nb.index not in seen:
                        seen.add(nb.index)
                        stack.append(nb)
        out.append(isl)
    return out


def clean(bm):
    """Merge coincident vertices, rejoin quads, point every face outward, flat shading."""
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.01)
    # glTF arrives as triangles; put the flat pairs back together as quads.
    bmesh.ops.join_triangles(bm, faces=bm.faces, angle_face_threshold=math.radians(1.0),
                             angle_shape_threshold=math.radians(180.0))
    # recalc_face_normals fixes the winding but leaves face.normal stale; every check below reads normals,
    # so refresh them first. (Open-bottomed boxes can come out of recalc inside-out; the checks catch it.)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.normal_update()
    bm.faces.ensure_lookup_table()
    flipped = 0
    for isl in islands(bm):
        c = sum((f.calc_center_median() for f in isl), Vector()) / len(isl)
        for f in isl:
            n = f.normal
            if n.z < -0.7:                          # no floors here: a downward face is a flipped roof
                f.normal_flip()
                flipped += 1
            elif abs(n.z) < 0.5 and (f.calc_center_median() - c).dot(n) < 0:
                f.normal_flip()
                flipped += 1
    bm.normal_update()
    for f in bm.faces:
        f.smooth = False
    return flipped


def wall_uv(f, uv, bay_w, floor_h, tile_bays, tile_floors, z0=None):
    n = f.normal
    t = Vector((0, 0, 1)).cross(n)
    t = t.normalized() if t.length > 1e-6 else Vector((1, 0, 0))
    s = [l.vert.co.dot(t) for l in f.loops]
    z = [l.vert.co.z for l in f.loops]
    smin, w = min(s), max(max(s) - min(s), 1e-3)
    zmin = min(z) if z0 is None else z0
    h = max(max(z) - zmin, 1e-3)
    nb = max(1, round(w / bay_w)) if bay_w else 1
    nf = max(1, round(h / floor_h)) if floor_h else 1
    for l, si, zi in zip(f.loops, s, z):
        l[uv].uv = ((si - smin) / w * nb / tile_bays, (zi - zmin) / h * nf / tile_floors)


def top_uv(f, uv, tile):
    for l in f.loops:
        l[uv].uv = (l.vert.co.x / tile, l.vert.co.y / tile)


# --- Skyline ---
slot_of = {m.name: i for i, m in enumerate(skyline.data.materials)}
facade_for_slot = {slot_of.get("Skyline_A", 0): 0, slot_of.get("Skyline_B", 1): 1, slot_of.get("Skyline_C", 2): 2}
bm = bmesh.new()
bm.from_mesh(skyline.data)
flipped = clean(bm)
uv = bm.loops.layers.uv.new("UVMap")
bm.faces.ensure_lookup_table()
blocks = islands(bm)
roofs = []
for isl in blocks:
    for f in isl:
        if f.normal.z > 0.7:
            f.material_index = 3
            top_uv(f, uv, 20.0)
            roofs.append(f)
        else:
            f.material_index = facade_for_slot.get(f.material_index, 0)
            wall_uv(f, uv, 4.0, 3.6, 4, 3)
# Rooftop bulkheads on about half the blocks.
added = 0
for f in list(roofs):
    if rng.random() > 0.55 or len(f.verts) != 4:
        continue
    v = [x.co.copy() for x in f.verts]
    e0, e1 = v[1] - v[0], v[3] - v[0]
    c = f.calc_center_median() + e0 * rng.uniform(-0.2, 0.2) + e1 * rng.uniform(-0.2, 0.2)
    a, b, hgt = e0 * rng.uniform(0.1, 0.17), e1 * rng.uniform(0.1, 0.17), rng.uniform(2.5, 4.5)
    corners = [c - a - b, c + a - b, c + a + b, c - a + b]
    low = [bm.verts.new(p) for p in corners]
    high = [bm.verts.new(p + Vector((0, 0, hgt))) for p in corners]
    new_faces = [bm.faces.new((low[i], low[(i + 1) % 4], high[(i + 1) % 4], high[i])) for i in range(4)]
    new_faces.append(bm.faces.new(high))
    bmesh.ops.recalc_face_normals(bm, faces=new_faces)
    for nf in new_faces:
        nf.normal_update()
    for nf in new_faces:
        c2 = sum((x.co for x in nf.verts), Vector()) / len(nf.verts)
        if abs(nf.normal.z) < 0.5 and (c2 - c).dot(nf.normal) < 0:
            nf.normal_flip()
        if nf.normal.z < -0.5:
            nf.normal_flip()
        nf.normal_update()
        nf.material_index = 3
        nf.smooth = False
        if nf.normal.z > 0.7:
            top_uv(nf, uv, 20.0)
        else:                                       # roof texture in metres on the walls too
            t = Vector((0, 0, 1)).cross(nf.normal).normalized()
            for l in nf.loops:
                l[uv].uv = (l.vert.co.dot(t) / 20.0, (l.vert.co.z - c.z) / 20.0)
    added += 1
bm.normal_update()
bm.to_mesh(skyline.data)
bm.free()
say(f"Skyline: {len(blocks)} blocks, {len(roofs)} roofs, {added} bulkheads added, {flipped} faces turned outward")

# --- Obelisk ---
bm = bmesh.new()
bm.from_mesh(obelisk.data)
flipped_ob = clean(bm)
uv = bm.loops.layers.uv.new("UVMap")
zb = min(v.co.z for v in bm.verts)
height = max(v.co.z for v in bm.verts) - zb
for f in bm.faces:
    n = f.normal
    t = Vector((0, 0, 1)).cross(n).normalized()
    s = [l.vert.co.dot(t) for l in f.loops]
    for l, si in zip(f.loops, s):
        l[uv].uv = ((si - min(s)) / max(max(s) - min(s), 1e-3), (l.vert.co.z - zb) / height)
    f.material_index = 0
bm.to_mesh(obelisk.data)
bm.free()
say(f"Obelisk: {height:.0f} m, {flipped_ob} faces turned outward")

# --- City ground ---
me = bpy.data.meshes.new("CityGround")
bm = bmesh.new()
uv = bm.loops.layers.uv.new("UVMap")
radii = [410, 520, 650, 800, 1000, 1250, 1550, 1900, 2400]
segs = 144
z = (lawn_top if lawn else centre.z) + 0.25
rings = [[bm.verts.new((centre.x + r * math.cos(2 * math.pi * k / segs), centre.y + r * math.sin(2 * math.pi * k / segs), z))
          for k in range(segs)] for r in radii]
mall_dir = math.atan2(-1.0, 0.0)          # Unity +Z (the Mall) is Blender -Y
mall_faces = 0
for i in range(len(radii) - 1):
    for k in range(segs):
        k2 = (k + 1) % segs
        mid = 2 * math.pi * (k + 0.5) / segs
        in_mall = abs((mid - mall_dir + math.pi) % (2 * math.pi) - math.pi) < math.radians(32)
        if in_mall and radii[i + 1] <= lawn_half:
            continue                        # the lawn itself carries on down the Mall
        f = bm.faces.new((rings[i][k], rings[i][k2], rings[i + 1][k2], rings[i + 1][k]))
        f.normal_update()                   # a new face has no normal until asked
        if f.normal.z < 0:
            f.normal_flip()
        f.material_index = 1 if in_mall else 0
        mall_faces += in_mall
        for l in f.loops:
            l[uv].uv = ((l.vert.co.x - centre.x) / 90.0, (l.vert.co.y - centre.y) / 90.0)
        f.smooth = False
bm.to_mesh(me)
bm.free()
ground = bpy.data.objects.new("CityGround", me)
say(f"CityGround: {len(me.polygons)} faces from {radii[0]} m to {radii[-1]} m at z {z:.2f} ({mall_faces} Mall faces)")

# ================================================================ materials
prev_engine = sc.render.engine
sc.render.engine = "CYCLES"
sc.cycles.device = "CPU"
sc.cycles.samples = 8
sc.render.bake.margin = 0
fac = [bake(name, facade(p), (512, 512), (256, 256)) for name, p in FACADES.items()]
roof_mat = bake("Roof", roof, (256, 256), (128, 128))
marble_mat = bake("Marble", marble, (256, 1024), (128, 512))
city_mat = bake("CityGround", city_ground, (512, 512), (256, 256))
lawn_colour = (0.33, 0.50, 0.22)
lawn_img = next((i for i in bpy.data.images if "Lawn" in i.name and "BaseColor" in i.name), None)
if lawn_img and lawn_img.size[0]:
    px = np.array(lawn_img.pixels[:]).reshape(-1, 4)[:, :3].mean(0)          # already sRGB-encoded bytes -> floats
    lawn_colour = tuple(float(c) for c in px)
    say(f"Mall grass matched to the lawn texture's average colour {tuple(round(c, 2) for c in lawn_colour)}")
mall_mat = bake("MallGrass", mall_grass(lawn_colour), (256, 256), (128, 128))
sc.render.engine = prev_engine
bpy.data.objects.remove(bake_plane)

def set_materials(mesh, mats):
    """Swap the material list without losing which face uses which slot: clearing the list also drops the
    per-face material index, so read it first and write it back."""
    idx = np.zeros(len(mesh.polygons), dtype=np.int32)
    mesh.polygons.foreach_get("material_index", idx)
    mesh.materials.clear()
    for m in mats:
        mesh.materials.append(m)
    mesh.polygons.foreach_set("material_index", idx)
    mesh.update()
    return np.bincount(idx, minlength=len(mats)).tolist()


say(f"Skyline faces per material (limestone, brick, glass, roof): {set_materials(skyline.data, fac + [roof_mat])}")
set_materials(obelisk.data, [marble_mat])
say(f"CityGround faces per material (city, Mall grass): {set_materials(me, [city_mat, mall_mat])}")

# ================================================================ lightmap UVs, pivots, collection
for o in (skyline, obelisk, ground):
    if o.name not in bpy.context.scene.collection.all_objects:
        sc.collection.objects.link(o)
    lm = o.data.uv_layers.new(name="UVLightmap")
    o.data.uv_layers.active = lm
    for x in bpy.context.view_layer.objects:
        x.select_set(x == o)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=4 / 1024, scale_to_bounds=True)
    bpy.ops.object.mode_set(mode="OBJECT")
    o.data.uv_layers.active = o.data.uv_layers["UVMap"]
    o.data.uv_layers["UVMap"].active_render = True
    # Origin at the city centre on the ground: the one point Unity's builder knows.
    o.data.transform(Matrix.Translation(-centre))
    o.matrix_world = Matrix.Identity(4)

coll = bpy.data.collections.get("Surroundings") or bpy.data.collections.new("Surroundings")
if coll.name not in sc.collection.children:
    sc.collection.children.link(coll)
root = bpy.data.objects.new("Surroundings_City", None)
coll.objects.link(root)
root.location = centre
for o in (skyline, obelisk, ground):
    for c in list(o.users_collection):
        c.objects.unlink(o)
    coll.objects.link(o)
    o.parent = root
old_root = bpy.data.objects.get("Surroundings")
if old_root and old_root.type == "EMPTY" and not old_root.children:
    bpy.data.objects.remove(old_root)
for m in [m for m in bpy.data.materials if m.name.startswith("Skyline_") and m.users == 0]:
    bpy.data.materials.remove(m)
try:
    bpy.ops.outliner.orphans_purge(do_recursive=True)
except RuntimeError as ex:
    say(f"orphan purge skipped: {ex}")

# ================================================================ check facing, then export
for o in (skyline, obelisk, ground):
    nz = np.empty(len(o.data.polygons) * 3, dtype=np.float32)
    o.data.polygons.foreach_get("normal", nz)
    nz = nz.reshape(-1, 3)[:, 2]
    say(f"facing {o.name}: {int((nz > 0.5).sum())} up, {int((nz < -0.5).sum())} down, {int((abs(nz) <= 0.5).sum())} sideways")
    if (nz < -0.5).any():
        raise SystemExit(f"{o.name} has downward faces; nothing here has a floor")
    cn = np.empty(len(o.data.loops) * 3, dtype=np.float32)
    o.data.corner_normals.foreach_get("vector", cn)
    fn = np.repeat(np.array([p.normal[:] for p in o.data.polygons]), [p.loop_total for p in o.data.polygons], axis=0)
    off = int(((cn.reshape(-1, 3) * fn).sum(1) < 0.99).sum())
    say(f"  {o.name}: {off} corner normals that disagree with their face (these are what the glb carries)")
    if off:
        raise SystemExit(f"{o.name}: exported normals would not match the faces")

# ================================================================ export
root.location = Vector((0, 0, 0))          # exported relative to the centre; Unity places it there
for x in bpy.context.view_layer.objects:
    x.select_set(x in (root, skyline, obelisk, ground))
os.makedirs(os.path.dirname(glb_path), exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=glb_path, export_format="GLB", use_selection=True, export_yup=True, export_apply=True,
    export_texcoords=True, export_normals=True, export_tangents=True, export_materials="EXPORT",
    export_image_format="AUTO", export_animations=False)
root.location = centre
tris = sum(len(p.vertices) - 2 for o in (skyline, obelisk, ground) for p in o.data.polygons)
say(f"exported {glb_path} ({os.path.getsize(glb_path) / 1e6:.1f} MB, {tris} triangles)")

# ================================================================ save and check
cam = bpy.data.objects.new("City Drone", bpy.data.cameras.new("City Drone"))
(bpy.data.collections.get("Lighting") or sc.collection).objects.link(cam)
cam.data.lens, cam.data.clip_end = 30, 5000
eye, look = Vector((-260, -330, 170)), Vector((60, 150, 10))      # Unity (260,170,330) -> (-60,10,-150)
cam.location = eye
cam.rotation_euler = (look - eye).to_track_quat("-Z", "Y").to_euler()
bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)
say(f"saved {blend_path}")
sc.camera = cam
sc.render.resolution_percentage = 50
sc.render.filepath = os.path.splitext(blend_path)[0] + "_city_drone.png"
bpy.ops.render.render(write_still=True)
say(f"preview {sc.render.filepath} ({time.time() - t0:.0f}s)")
say("DONE")
