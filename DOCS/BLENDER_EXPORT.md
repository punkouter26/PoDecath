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

## Game-ready checks (2026-09-29)

Every model was audited against the usual export checklist; the full result, with before/after renders, is
`DOCS/reports/2026-09-29-model-realism-pass.html`. What matters for the next export:

- **Do not apply rotations or scale on the building.** 314 objects share 28 meshes (107 wing windows are
  one mesh); the rotations are instance placements, and applying them turns 28 meshes into 314.
- `training/tools/game_ready.py` is the building pass to run on a fresh export: flag and flagpole pivots to
  their foot and hoist (they sat 33 m and 22 m below), inverted-face repair, and weighted normals on the
  hard-surface parts. On the 2026-09-04 export the weighted normals changed under 2 % of pixels, so that
  export was left as it was; run the pass when the building is re-exported for another reason.
- `training/tools/athlete_glb_fix.py` patches a rigged athlete glb without re-exporting it (mesh, skeleton
  and textures stay byte-identical): no-map materials that glTF defaults to solid metal, emissive set to the
  model's own base colour, doubled specular, more than four bones per vertex, flat-shaded normals, and
  `--ao` bakes an occlusion map. Run it on every new AI-generated athlete before it goes in the roster.
- `training/tools/model_audit.py` reports all of the above for any glb/fbx; `render_preview.py` makes
  matching before/after stills.

## Unity scene -> new .blend (2026-09-29)

A Unity scene can be rebuilt as a new, stand-alone Blender file (it never touches the owner's .blend):

1. With the editor open: `unity command eval_file --file training/tools/unity_scene_export.cs --timeout 600000`
   (edit `SCENE` at the top for another scene). It opens the scene alongside the current one without saving
   it, exports the generated geometry, sun and camera with glTFast to `Blender/source/<Scene>_generated.glb`,
   and writes `<Scene>_scene.json`: which model files are placed where, the sky, ambient, fog and the global
   Volume's grading. If the result says the export is "still running", it finishes on its own a few seconds
   later; close the scene afterwards (`EditorSceneManager.CloseScene`).
2. `blender-launcher --background --python training/tools/unity_scene_to_blend.py -- Blender/source/<Scene>_scene.json Blender/<Scene>.blend`

The White House comes from `WhiteHouse.glb` at full detail (its LOD copies are skipped); the sky is the
scene's own HDR, turned to Unity's orientation; fog is Unity's linear fog as a mist step in the compositor
(renders show it, the viewport does not); grading is Neutral tonemapping plus the Volume's contrast and
saturation. Not carried over: bloom, vignette, baked lightmaps, and anything spawned at runtime (athletes,
hurdles, crowd). `Blender/RooftopRace.blend` (125 MB, textures packed) was made this way; it is not in git.

## The city around the grounds (2026-09-29)

`Assets/Models/Surroundings.glb` is the city ring made in Blender from the placeholder blocks
`SurroundingsBuilder` used to generate: limestone, brick and glass facades with windows, roofs with plant
bulkheads, the obelisk in marble with its change of stone at 46 m, and a street-and-block ground ring from
410 m out (grass down the Mall) so nothing past the lawn stands on air. 5,372 triangles, 1.5 MB, seven
materials, each Base Colour + ORM + normal. `SurroundingsBuilder` places it at the city centre when the file
exists (static, no shadows, no probes, kept out of the light bake) and falls back to the old boxes when not;
the treeline is still generated.

To change it: edit the `*_Procedural` materials or the numbers in `training/tools/improve_surroundings.py`,
then run it on the pre-city scene (`Blender/source/RooftopRace_before_city.blend`, a copy of what
`unity_scene_to_blend.py` produced) and reimport; the four rooftop scenes pick the model up without a
rebuild, since they hold it as a prefab instance:

    copy Blender/source/RooftopRace_before_city.blend -> Blender/RooftopRace.blend
    blender-launcher --background --python training/tools/improve_surroundings.py -- Blender/RooftopRace.blend Assets/Models/Surroundings.glb

The script refuses to export if any face points down or any exported normal disagrees with its face (a
downward-facing ground is culled from above and simply vanishes in Unity; Blender's render shows both sides
and hides the problem). Before/after stills come from `training/tools/unity_scene_shots.cs`.

## Where Blender is

Blender 5.2.2 LTS is installed from the Microsoft Store (2026-09-29). Its `blender.exe` cannot be started
from its install folder; run it as `blender-launcher` (the app alias in `%LOCALAPPDATA%\Microsoft\WindowsApps`).
The launcher does not pass Blender's printed output back to the terminal, which is why every script here
also writes a log under `training/logs/`. The Blender MCP add-on (`mcp__blender__*`) is not installed.
