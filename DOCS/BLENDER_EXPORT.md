# The White House: from the owner's .blend to the game

The whole look of every rooftop scene comes from one file, `Assets/Models/WhiteHouse.glb` (117 MB), which
is an export of a Blender scene that exists on the owner's machine and nowhere else. The `.blend` is the
owner's asset: **export from it, never modify or save it** (`CLAUDE.md`). This page records how the export
is made, so that a re-export gives the game the same building it has now — and so the building can be
rebuilt if the `.glb` is ever lost.

Nothing in the repo could answer that question before this page existed. Treat it as the recipe; when
the recipe changes, change the page.

## What the game needs from the file

The Unity side reads the file through glTFast and then does three things to it that depend on how it was
exported. If any of these break, the scene builds and looks wrong rather than failing, so check them.

1. **Object names, by prefix.** `training/tools/whitehouse_lods.py` decides how hard to cut each part
   from the first word of its name, and `BuildingLodBuilder` matches LOD renderers to full-detail
   renderers by exact object name. The prefixes the pipeline knows are:

   | Prefix | What it is | LOD1 | LOD2 |
   |---|---|---|---|
   | `Residence`, `Wings`, `SouthPortico`, `NorthPortico` | the building shells | 35 % | 12 % |
   | `Tree_` | the 38 trees, 400k of the 833k triangles | 20 % | 6 % |
   | `W_` | every window segment, one mesh each | kept | 15 % |
   | `Grounds_Hard` | paving, the roof deck the track stands on | kept | kept |
   | `Grounds_Props`, `Detail_Fixed` | lamps, benches, fixed detail | 30 % | dropped |
   | `Car_`, `Flag` | cars on the drive, flags | kept | dropped |
   | anything else | | 50 % | 15 % |

   A renamed object falls into "anything else". A part with no LOD counterpart is drawn at full detail
   on every tier, silently.

2. **Material names.** The LOD files ship with no textures; Unity points every LOD renderer at the
   LOD0 material *of the same name*, so the 100 MB of textures are in the build once. Renaming a
   material in Blender means the LOD copy of that part goes grey.

3. **+Y up, metres, applied transforms.** The kart track is solved against the roof by raycast
   (`KartTrackBuilder`), the deck legs are ray-cast onto the roof, and the athlete is 1.8 m tall. A
   scale or an unapplied rotation on the building moves the track off the roof.

## Export settings (Blender glTF exporter)

`File > Export > glTF 2.0`, with:

| Setting | Value | Why |
|---|---|---|
| Format | **glTF Binary (.glb)** | one file, textures embedded |
| Include > Limit to | nothing ticked (whole scene) | the LOD script re-imports the whole thing |
| Transform > +Y Up | **on** | Unity's axis |
| Mesh > Apply Modifiers | **on** | the LOD script applies its own on top |
| Mesh > UVs, Normals | **on** | the world-metre UV projection is done at runtime, but the model's own UVs carry the textures |
| Mesh > Tangents | off | Unity computes them; the facade bake exports its own with tangents on |
| Material > Materials | Export | by name, see above |
| Material > Images | **Automatic** | embed the textures; this is most of the 117 MB |
| Animation | **off** | nothing in the building moves |
| Compression (Draco) | off by default — see below | |

Then `git lfs` handles the file: `*.glb` is an LFS object (`.gitattributes`), so commit it as normal and
**on any fresh checkout run `git lfs pull`** or the building is a 134-byte pointer and the scene is empty
(`DOCS/README.md`, "Running things").

### Optional: compression

Unity has `com.unity.cloud.draco`, `com.unity.meshopt.decompress` and `com.unity.cloud.ktx` installed
(`AGENTS.md`), so the exporter's Draco mesh compression and KTX2 texture compression are both readable.
Neither is on today. Textures, not triangles, are most of the file, so **KTX2/Basis on the images** is the
one that would matter (expect 117 MB to drop to 30–40 MB). It changes nothing in the pipeline above and
is worth doing once the look is settled; until then a plain export is easier to inspect.

## After every re-export

The building's derived files are all made *from the glb*, never from the `.blend`, so they all go stale
when it changes. In order:

1. `blender --background --python training/tools/whitehouse_lods.py -- Assets/Models/WhiteHouse.glb Assets/Models`
   — regenerates `WhiteHouse_LOD1.glb` and `WhiteHouse_LOD2.glb`. Minutes.
2. (When the phone draws LOD2) `blender --background --python training/tools/whitehouse_facade_bake.py`
   — bakes the full-detail relief and colour onto the LOD2 shell as textures under
   `Assets/Textures/Building/`. Longer; the log says where it is.
3. In Unity, `PoDecath/Build Rooftop Scene` (and the lap, race and long jump variants) — reattaches the
   LODs and materials. Scene rebuilds discard lightmaps, so then:
4. `PoDecath/Bake Lighting`. Minutes.
5. `PoDecath/Sweep All Scenes` — and compare its triangle counts to the ones in `DOCS/README.md` before
   believing the building is still under budget.

## Where Blender is

At the time of writing (2026-09-14) Blender is **not installed on this machine**: `blender.exe` is not in
Program Files, Steam, the Store or the user profile, though `%APPDATA%\Blender Foundation\Blender\5.0`
and `5.2` show it was. Install 4.2 LTS or later from blender.org (the scripts use `modifiers.move` and
the glTF exporter's `export_image_format`, both fine from 4.x). The Blender MCP add-on this project's
tooling can drive (`mcp__blender__*`) also needs Blender open with the add-on enabled.
