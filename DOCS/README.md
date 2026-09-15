# PoDecath — project summary

A 3D "summer games" in the spirit of the Atari 800 Decathlon: several mini-events, all athletes CPU
controlled. Athletes are physics-driven humanoids whose locomotion policies are trained **outside Unity**
in **MuJoCo Warp** and executed in Unity through the Inference Engine (ONNX). Unity is the visual runtime
and game layer only; no ML-Agents.

Setting: the owner's Blender **White House** scene (`Assets/Models/WhiteHouse.glb`, re-exported 2026-09-04 19:01,
122 MB with textures, grounds, cars, lamps and people) with a **go-kart race track built in ProBuilder on the
residence roof only**. `KartTrackBuilder` solves a stadium loop covering 40% of the 51 x 26 m roof plan:
22 m straights, 8.8 m curve radius, 100 m lap, 5.3 m deck on legs ray-cast onto the roof. The dash event
takes the straight it gets (20.4 m, five 1.08 m lanes), not a fixed 100 m. Portrait (9:16) mobile layout is
kept from the original runtime scaffold. Re-export the glb from Blender whenever the `.blend` changes; the
`.blend` itself is never saved by tooling.

## Agreed plan (2026-09-04 interview)

- First event: **one lap of the rooftop track** as built (100.1 m), one humanoid, more humanoids added
  later from their own rigged glb files (`AthleteDefinition.skinOverride` + `boneMap`). Later events:
  long jump, high jump, hurdles.
- Hands-off: watch the race, one Restart button. HUD = lap timer / finish time, speed, distance along the
  lap, stability (turns red on a fall). Falls reset after 3 s, finishes after 4 s.
- Lap scene: `Assets/Scenes/RooftopLap.unity` from menu `PoDecath/Build Rooftop Lap Scene`.
- Verified: final lap policy (`athlete_track.onnx`, 2300 iterations) laps in 24.2 s, within 1 m of the
  centre line, no falls.
- Trainer policy (2026-09-14): MuJoCo Warp is the only trainer. A second trainer, its twin athlete and
  its comparison tool were removed by owner decision; do not reintroduce a second training path.
- Mobile performance (Task 8): measured 2026-09-12 by `PoDecath/Sweep All Scenes`, the rooftop scenes
  render **2.0-2.4M triangles** at 183-363 draw calls (Rooftop 2,389,352 / 363; RooftopLap 2,152,268 / 248;
  RooftopRace 2,040,756 / 183; RooftopLongJump 953,898 / 147). The older figure here, ~837k triangles and
  ~700 batches, predates the 2026-09-04 glb re-export and was wrong by roughly 2.7x; do not plan against it.
  Static batching is on and the target is 60 FPS, but against a mobile budget of under 200k on screen that
  is a **10x gap**. **2026-09-12, later the same day:** the building now has LODs
  (`training/tools/whitehouse_lods.py`: 833k -> 273k -> 68k triangles in the glb) and the mobile tier never
  draws LOD0 (`QualitySettings.maximumLODLevel = 1`, set by `RenderTier.Apply`, which `SceneLook` now calls
  in every scene), and the building casts no shadows on that tier (`SceneLook.ApplyBuildingShadows`). Measured
  by the same sweep with the tier forced to Mobile: RooftopRace 2,496,682 -> **712,488**, RooftopLongJump
  1,687,886 -> **282,040**, RooftopLap 2,051,830 -> 386,244, Rooftop 2,282,650 -> 520,038 (sweep triangles
  count every pass, shadows included; the LOD ceiling alone gave 1,105,028 on the race scene, the shadow
  change the rest). Still 3.5x over the 200k target on the race scene: the next levers are LOD2 as the
  phone's building (the window segments are separate meshes, so check the facades close up before dropping
  them) and the athlete skins themselves. Re-run the sweep after any change to the building and compare,
  rather than estimating.

## Phase 1 scope (current)

| Piece | Where | Status |
|---|---|---|
| Athlete skinned mesh (Matt, Mixamo rig) | `Assets/Models/Athlete_Matt.glb` | provided by owner |
| Physics rig generated from the rig bones (21 DoF) | `training/rig_to_mjcf.py` -> `training/models/athlete.xml` | done |
| Run-to-target training (PPO on MuJoCo Warp, 4096 envs) | `training/train_run.py`, `training/envs/run_to_target.py` | runs, exports `Assets/Policies/athlete_run.onnx` |
| 100 m evaluation in MuJoCo | `training/eval_100m.py` | done |
| MJCF -> ArticulationBody importer + skin binding | `MjcfImporter.cs`, `SkinBinder.cs` | done |
| Rooftop kart track (ProBuilder) | `KartTrackBuilder.cs` via menu `PoDecath/Build Rooftop Scene` | done |
| 100 m dash event with the RL athletes (the heuristic-coded sprinter was removed on 2026-09-14, owner decision) | `RaceEvent.cs`, `AthleteSpawner.cs` | done |
| Lap race around the roof loop (carrot follower + lap policy) | `TrackPath.cs`, `TrackFollower.cs`, `LapEvent.cs`, `training/envs/run_track.py`, menu `PoDecath/Build Rooftop Lap Scene` | done: 100 m lap in 25 s, no falls |
| Balance / get-up policy: fallen-pose resets on a widening tilt curriculum, reward on uprightness then height then a one-second hold, timeout-only termination | `training/envs/get_up.py`, `train_run.py --task getup` | trains well **in MuJoCo**: 2400 iterations gives stood 1.00, standing 78% of steps, a held second 59% of steps, at full difficulty (flat on the back). Exports `Assets/Policies/athlete_getup.onnx` |
| Recovery at runtime: a fallen athlete switches to the get-up policy and rejoins the race instead of taking a DNF; gives up after a timeout so a wedged body cannot stall the event | `RecoveryController.cs`, `PolicyRunner.recoveryModel`, `RaceEvent.DetectFall` | plumbing verified end to end in play mode (fall -> get-up policy drives -> give-up -> DNF -> race continues). **Resolved 2026-09-06: the policy transfers and always did.** Measured with `PoDecath/Probe Get-Up Transfer`, which drops the athlete flat on its back and records every physics step: from upright 0.051 it reaches **peak uprightness 0.947 and holds the stand 7.44 s of 8**. The bug was in the watcher, not the policy -- `RecoveryController.FloorY` raycast with mask `~0` hit the athlete's own chest collider, so `heightFrac` went negative and `standing`, the only exit from `Recovering`, could never be true. Every athlete that stood up was driven to `giveUpSeconds` and booked a DNF anyway. One layer mask; before/after reads **recoveries 0 -> 3** |
| Long jump on an infield deck inside the loop (ProBuilder runway, board, recessed sand pit; sequential attempts, 3 rounds, best mark; broadcast cuts + results modal; picked from the setup menu) | `LongJumpBuilder.cs`, `LongJumpPit.cs`, `LongJumpEvent.cs`, menu `PoDecath/Build Long Jump Scene` | done: scene `Assets/Scenes/RooftopLongJump.unity`; take-off impulse scripted until a jump policy exists |
| Lap distances: 400 m (4 laps) and 1500 m (15 laps), picked on the menu; one scene, `SessionSettings.Laps` | `LapEvent`, `SetupView` | done |
| Hurdles: 0.762 m bars on 9 kg toppling frames along the straights, knocks booked per runner | `HurdleSet.cs`, `Hurdle.cs` | playable; no policy clears one yet |
| Broadcast overlay: clock + lap counter, live running order that animates a pass, lap splits, lower third that wipes in on every cut, punching countdown | `Assets/UI/Broadcast.uxml`, `BroadcastView.cs` | done |
| UI Toolkit screens: setup menu, HUD, broadcast overlay, results card, main menu, diagnostics — one stylesheet, one panel | `Assets/UI/*.uxml` + `Theme.uss`, `UiRoot.cs` and the `*View.cs` drivers, `UiBakery.cs` | done; the uGUI versions are gone |
| Look: graded post stack per tier, procedural sky, three time-of-day presets, fog, reflection probe, focus racked onto whoever is on air | `LookBakery.cs` -> `Assets/Settings/Broadcast_Volume_*.asset`, `SceneLook.cs`, `CinematicFocus.cs` | done |
| Surfaces: procedural albedo/normal/mask for asphalt, concrete, rubber, sand, turf, metal, paint; mesh UVs re-projected in world metres | `TextureBakery.cs` -> `Assets/Textures/`, `WorldUvProjector.cs` | done |
| Quality tiers: mobile (0.8 scale, 1 cascade, blob shadows) and PC (MSAA 4x, soft shadows, SSAO, depth of field), one switch | `RenderTier.cs`, `LookBakery.TuneRenderPipelineAssets` | done |
| VFX: gun smoke, sand burst, fall dust, hurdle sparks, finish confetti, per-footfall dust; all pooled and built in code at the tier's budget | `VfxBakery.cs` -> `Assets/Materials/Fx_*`, `VfxLibrary.cs`, `RaceVfx.cs`, `FootstepDust.cs` | done |
| Athlete presentation: lane-colour trail ribbon, contact/blob shadow — identity without tinting skin | `AthleteTrail.cs`, `BlobShadow.cs` | done |
| Audio: synthesised crowd bed/swell/groan/applause/chant, wind, pistol, countdown, bell, clatter, hurdle clip, sand, whoosh, sting, breathing, 10 footfalls across 3 surfaces | `AudioBakery.cs` -> `Assets/Audio/` | done |
| Spatial mix: crowd ring round the deck, 3D one-shot cue pool, code buses with ducking, reverb + distance low pass on the listener, crowd mood machine | `CrowdRing.cs`, `SpatialCue.cs`, `AudioMix.cs`, `ListenerAcoustics.cs`, `RaceAudio.cs` | done |
| Diagnostics overlay (F3): FPS + 1% low + frame graph, draw calls / batches / SetPass / triangles, GC per frame, memory, athlete and audio-voice counts, crowd mood | `TelemetryOverlay.cs`, `Assets/UI/Telemetry.uxml` | done |
| Effort and strain: each joint's drive torque against the force limit it was configured with, the mechanical power that implies, and a cosmetic fatigue built off it. Drawn on the body as a per-joint glow, on the overlay as a strain bar in watts, and heard in the breathing. **Reads only — nothing downstream may touch a drive** | `EffortMeter.cs`, `StressSkeleton.cs`, `FootstepAudio.cs`, `BroadcastView.Strain` | done; not yet checked on a phone |
| Drama: one measured tension number (gap, closing rate, lateral acceleration, uprightness trend, recent incidents) that the gallery and the crowd both read. The cut rate rides it, and the director will cut to a runner a beat **before** it goes down | `DramaMeter.cs`, `BroadcastDirector.Anticipate`, `RaceAudio` | done; the anticipation threshold is a guess and wants watching |
| Commentary: a priority slot (not a queue) fed by the gun, falls, recoveries, lead changes, splits, the bell, hurdles, marks and finishes, plus colour off the strain and drama readings. Captions always; a spoken voice on Android and Windows only | `Commentary.cs`, `SpeechSynth.cs`, `Assets/Plugins/iOS/PoDecathSpeech.mm`, `Assets/UI/Broadcast.uxml` | done on every platform that has a system voice: Android `TextToSpeech`, Windows `System.Speech`, macOS `say`, iOS `AVSpeechSynthesizer` (2026-09-14, native plugin), Linux `spd-say`/`espeak-ng`/`espeak` if installed. Only WebGL and consoles are captions-only. The Mac, iOS and Linux voices have not been heard yet — no such device here |
| Impacts scaled by the physics: contact impulse drives hurdle sparks and camera shake, foot slip leaves skid marks and throws dust along the slide | `Hurdle.LastImpulse`, `FootContactSensor.SlipSpeed`, `CameraShake.cs`, `SkidMarks.cs`, `RaceVfx.cs` | done; `referenceImpulse` is a first guess. **`TuningLog.cs` (2026-09-14) now writes `training/logs/races/tuning_*.json` after every race** with every hurdle impulse (clip or topple), each athlete's joules at the finish and the drama reading before every fall, and prints a one-line verdict against `referenceImpulse`, `fatigueJoules` and `anticipateRisk`. Run one race, read the line, set the three numbers |
| Character roster: every rigged model in `Assets/Models/Characters` becomes an athlete, bone map and facing worked out from the skeleton's shape, skin sized to the rig at bind time | `SkeletonMapper.cs`, `AthleteRosterBuilder.cs`, menu `PoDecath/Rebuild Athlete Roster` | done: 8 models (Matt Avaturn, Grandma, Grandpa, Matt, Nick, Nick Doggy, Trump, Zombie Accurig), all 12 bodies bound on each, every one within 3 degrees of square |
| Picking the field: one counter per roster entry on the setup menu, ONE EACH / NONE, 1-16 runners | `SetupView.cs`, `Assets/UI/Setup.uxml` | done |
| Building LODs: the White House decimated in Blender from the exported glb (never the .blend) to 33% and 8% of its 833k triangles, one LODGroup per part; the mobile tier never draws LOD0 at all and the building stops casting shadows there | `training/tools/whitehouse_lods.py` -> `Assets/Models/WhiteHouse_LOD1/2.glb`, `BuildingLodBuilder.cs`, `RenderTier.Apply` | done; see the mobile numbers below. **Next step is staged (2026-09-14):** the phone still draws LOD1 because the shipped LOD2 deletes every window; the LOD script now keeps them (`W_` at 15 %), `whitehouse_facade_bake.py` bakes the full building's relief and colour onto the LOD2 shell, `BuildingLodBuilder.ApplyFacadeBakes` assigns the result, and `RenderTier.MobileLodCeiling` is the one number to raise from 1 to 2 once both scripts have run in Blender and the sweep has been re-measured. Blender is not installed on this machine; see `DOCS/BLENDER_EXPORT.md` |
| Athlete LODs for the phone: every rigged model decimated to a triangle budget with its skeleton, weights and material names intact and its textures capped at 1k, wired per athlete as `AthleteDefinition.skinOverrideMobile` and drawn only on the mobile tier | `training/tools/athlete_lods.py` -> `Assets/Models/Characters/Mobile/*_LOD1.glb`, `AthleteRosterBuilder`, `AthleteSpawner.SpawnBody` | code done 2026-09-14, **script not yet run** (no Blender). Falls back to the full skin until it has; sixteen athletes at the 8k default is 128k triangles, which with the LOD2 building is at the 200k target |
| The building's export recipe, which was nowhere: glTF settings, the object-name prefixes the LOD pipeline keys on, the order to regenerate everything after a re-export | `DOCS/BLENDER_EXPORT.md` | written 2026-09-14 |
| Replacing the synthesised audio: what every bank slot wants, in what order, and how to drop one in | `DOCS/AUDIO_REPLACEMENT.md` | written 2026-09-14; no samples sourced |
| Photographed skies (three Poly Haven HDRIs, one per time-of-day preset), a skyline ring, the obelisk and a treeline past the grounds, lit windows at night | `Assets/Textures/Sky/`, `LookBakery.BakeHdriSkies`, `SurroundingsBuilder.cs`, `SceneLook.ApplyWindows` | done |
| Baked lighting: mixed sun with a shadowmask, lightmaps on the building and the track, a probe ring over the deck; the bake is a menu item because it takes minutes | `LightingBakery.cs`, menu `PoDecath/Bake Lighting`, `Assets/Settings/Rooftop_Lighting.lighting` | done; re-run after any scene rebuild, which discards lightmaps |
| A visible crowd: rows of billboard spectators on the roofs outside the rail, bouncing with the level the mix already computes and jumping on every cheer | `CrowdStands.cs`, `Assets/Shaders/CrowdBillboard.shader`, `RaceAudio.CrowdLevel` | done |
| Featured athlete: an additive rim on whoever the gallery is on, a sweat sheen that rises with fatigue, sweat spray off hard footfalls when tired. Nothing on the models' own materials | `AthleteSheen.cs`, `Assets/Shaders/AthleteRim.shader`, `VfxLibrary.Effect.Sweat` | done |
| Floodlights on four masts for the night preset, cascades split near so athlete shadows are crisp inside 40 m, heat shimmer over the straights on a PC afternoon | `Floodlights.cs`, `HeatHaze.cs`, `LookBakery.Tune` | done |
| Finish tape that breaks where the winner crossed and flutters on a verlet rope; bunting on the rail that flaps with the preset's wind | `FinishTape.cs`, `Bunting.cs`, `Assets/Shaders/Pennant.shader` | done |
| One-screen menus: the field is a grid of tiles sized to the height left, results go compact past eight rows, the broadcast overlay carries a track map with a dot per athlete beside the splits | `SetupView.FitTiles`, `ResultsView`, `BroadcastView.DrawMap`, `Setup/Results/Broadcast.uxml` | done |
| Occlusion on the listener (a solid line to the featured athlete closes the low pass), Doppler on footfalls muted across cuts, a frame-time and battery chip in the frame, one JSON per race with the frame-time record | `ListenerAcoustics`, `AudioMix.MuteDoppler`, `AppFrameView.Chip`, `RaceLog.cs` -> `training/logs/races/` and the device's `persistentDataPath/races/` | done |
| Other events (high jump, throws, ...) | `DOCS/ROADMAP.md` | placeholders |

## State-flow and framing review, 2026-09-13

Ten defects found by driving the live editor over the MCP bridge rather than by reading code, and fixed
in one pass. The two that mattered were invisible from the source and obvious from the running game.

**The results card had never worked.** `Results.uxml` line 6 mentioned the compact row class by name, two
hyphens and all, inside an XML comment. XML forbids `--` there, so the importer threw and the file landed
as an empty `VisualTreeAsset`: 0.2 kb against 8 to 16 kb for the other screens. Every lookup in
`ResultsView` failed, `Show` returned at its first guard, and no race in the game ever displayed a result.
It failed silently in both directions, because the scene still built, the event still ranked the field and
the console only carried seven "no 'root' in Results" warnings among eighteen other lines. `RooftopRace`
had `autoRestart` off on the reasoning that "the modal ends the race", so **every race ended in a state
with no exit at all**: phase `Finished`, for ever, until somebody pressed MENU.

Two guards now, and the second is the point: `RaceEvent.ResultsShown` is set only by a results card that
actually drew itself, nothing restarts a race while it is up (which is separately what `RooftopLap` used
to do, because it kept `autoRestart` on), and `deadEndSeconds` restarts a race whose card never reported
itself after 30 s and says loudly why. Verified by disabling the card at runtime: the race sat in
`Finished` and restarted itself 30 s later with the warning in the console.

**The post-processing did not exist.** `LookBakery` built each override with `VolumeProfile.Add` and never
wrote it into the asset, so both profiles serialised as a list of references to objects that were never
saved. On disk: `Broadcast_Volume_Mobile` was seven `{fileID: 0}` entries and `_PC` ten, the exact count
each recipe adds. At runtime the global volume loaded with **zero** components. No tonemapping, no bloom,
no grade, no vignette, no depth of field, on either tier, while this file recorded the look as done. It
also took `CinematicFocus` down with it every session: `Volume.profile` clones the shared profile, cloning
a list of destroyed entries throws, and the throw came out of `OnEnable`, so focus racking disabled itself
permanently behind one exception line. `LookBakery.Persist` now adds each override as a sub-asset; both
profiles carry their full set (7 and 10, no nulls).

**Camera angles were authored for the wrong axis.** Cinemachine's `Lens.FieldOfView` is the *vertical*
angle, and this game is portrait. Measured on the shipped gallery at 9:16, the stadium wide covered 13.7 m
across at 25 m, against a 22 m straight and a 51 x 26 m roof, and **2 athletes of 11 were inside the frame
at the finish**. `PortraitLens` now holds each shot's *horizontal* angle and solves the vertical one from
the live aspect every frame. Same race after: 8 of 11 in frame at the finish, 11 of 11 on the grid and
through the bend. The four trackside shots also sat at chest height where the barrier and its bunting
crossed the picture; their heights are named fields now, defaulted above the rail, and the director can
measure its own framing (`SubjectVisible`, `AthletesInFrame`).

**`IsRL` means "has a rig", and two places needed "is driven by a policy".** It stopped being an accurate
test of the second the day the heuristic bot got a physics body, and nothing noticed. `RaceEvent.Register`
handed the RED bot the reference slot, so on an eleven-strong field the HUD read 0.00 m/s, 0 % stability
and 0 of 100 m for a whole race while ten RL athletes ran clean laps behind it, and the shorter
`fallRestartDelay` was chosen every time. `AgentTelemetry` graded the same bot **Bad** for having no ONNX,
which is the entire point of it, and since it sorts first the DEBUG chip in the corner of every screen had
been red on every race ever run. Both now ask `IsPolicyDriven`. The chip reports the real finding instead:
**15 to 19 % of joint targets are clamped to the rig's limits on every RL athlete**, which is a genuine
open question about `actionScale` or rig limits and is deliberately left showing.

**Resolved 2026-09-14: that figure is the trained behaviour, not a fault.** The trainer clamps targets to
the actuator range in exactly the same way (`run_to_target.py`, `step`: `target = (default + action *
scale).clamp(ctrl_lo, ctrl_hi)`), and with `action_clip` 3.0 and `action_scale` 0.5 a policy can ask for
±1.5 rad against joints that allow ±0.79, so it lands on the stops routinely *in training too*. Unity
was reading the same number against a fixed 10 % bar and calling every healthy athlete Bad. The fix
measures the same thing on both sides: the env now reports `tgt_clamp`, `train_run.py` writes it into the
policy manifest as `train_target_clamp`, and `AgentTelemetry` grades the live figure against it — Bad only
when Unity clamps more than 8 points above what training did, which is a rig whose limits have drifted.
A manifest from before the field existed still gets the old fixed bar, with a verdict that says so. The
shipped policies predate the field, so the chip stays as it was until one is re-exported.

Also fixed: a race scene carried two visible MENU buttons going to two different scenes (the frame's to
`MAIN`, the HUD's to the retired `MainMenu`) - both point at `MAIN` and the HUD hides its own where the
frame is present; the setup screen padded clear of the frame's bottom row but not its top, so its title
was drawn under the game name and MENU, and it printed the product name a second time; `PolicyRunner`
warned about a dynamic model shape once per athlete rather than once per model, eighteen times in a field
of eleven; and `RaceLog` began recording on the gun, so the first attempt in a scene averaged shader
warm-up and policy initialisation in as gameplay and reported **46 FPS mean, a 1 % low of 2 FPS and a
worst frame of 3,456 ms** for a race the on-screen counter held at 59 FPS throughout. Warm-up and stalls
are now counted in their own buckets and reported separately rather than defining the headline.

Two things worth keeping: **never write `--` inside a UXML comment** (there is a warning in
`Results.uxml` saying so), and a feature the docs record as done is not evidence that it runs.

## Folder map

```
Assets/Scripts/Runtime/Sim      physics, policy runner, importer, events, athletes
Assets/Scripts/Runtime/UI       UI Toolkit screen drivers (UiRoot + one View per screen)
Assets/Scripts/Runtime/Audio    crowd ring, cue pool, mix buses, listener acoustics, footsteps
Assets/Scripts/Runtime/Fx       particle library, race VFX, athlete trails, blob shadows
Assets/Scripts/Runtime/Env      render tier, scene look (sun/sky/fog/volume), cinematic focus
Assets/Scripts/Runtime/Diag     the F3 telemetry overlay
Assets/UI                       UXML layouts, Theme.uss design tokens, panel + theme assets
Assets/Textures                 generated surface maps and particle sprites (PoDecath/Bake Surfaces, Bake Effects)
Assets/Audio                    generated clips + AudioBank (PoDecath/Bake Audio Clips)
Assets/Scripts/Editor           scene/prefab builders (PoDecath menu)
Assets/Models                   WhiteHouse.glb, Athlete_Matt.glb, athlete.xml (copied from training)
Assets/Models/Characters        rigged athlete models (.glb/.fbx); drop one in and rebuild the roster
Assets/Athletes                 one AthleteDefinition per roster entry (Athlete_*.asset, named after displayName)
Assets/Policies                 ONNX checkpoints + PolicyLibrary (auto-refreshed)
training/                       Python: MJCF generation, MuJoCo Warp env, PPO, eval, TensorBoard logs
DOCS/                           this summary and the roadmap
```

## Running things

- **First, on any fresh checkout: `git lfs pull`.** The `.glb` models are Git LFS objects
  (`.gitattributes`: `*.glb filter=lfs`), and a clone without them leaves 134-byte pointer files where
  `WhiteHouse.glb` (122,651,496 bytes) and `Athlete_Matt.glb` (3,262,100 bytes) should be. This fails
  quietly and expensively: the scenes still open, still enter play mode, still bind rigs and run their
  events, so nothing looks broken — but every rooftop scene logs `Missing Prefab Asset: 'WhiteHouse
  (guid d70df9d664107d142963865ea83d04a6)'`, the building is absent, the athletes fall back to red
  capsule primitives because `SkinBinder` has no skin to bind, and the triangle count drops to about 4%
  of the real figure. The physics rig comes from `athlete.xml`, which is not an LFS object, which is
  exactly why the athletes still run and the damage is easy to miss. If a sweep reports suspiciously low
  triangles, check `ls -la Assets/Models/*.glb` before anything else.
- Train: `cd training && .venv/Scripts/python.exe train_run.py --num-envs 4096 --iters 1500`
  (TensorBoard opens on http://localhost:6006; stale runs are cleared first).
- Train the get-up (exports `Assets/Policies/athlete_getup.onnx`):
  `.venv/Scripts/python.exe train_run.py --task getup --num-envs 4096 --iters 3000 --desired-kl 0.02 --entropy-coef 0.012`
  Domain randomisation is on by default and ramps from `--dr-start-strength` to full over
  `--dr-ramp-iters` (both curricula are measured from the start of *this* run, so a `--resume`
  fine-tune ramps from where it is rather than starting already finished). Two things worth knowing
  before reading a curve: the first ~500 iterations of a from-scratch get-up *look* like the
  lying-down local optimum -- return climbing while `stood` falls and the action noise collapses --
  and are not; the reference run in `training/logs/train_getup.log` sits at `stood` 0.03 at iteration
  230 and reaches `stood` 1.00 by 1520. And randomising from scratch fights the entropy rebound that
  escape depends on, so the cheaper route to a hardened policy is `--resume` from a checkpoint that
  already stands and let the ramp harden it.
  The curriculum and the raised entropy are not optional garnish: trained against the full range of fallen
  poses at a default entropy, the policy converges on lying down well — return climbs while it never once
  holds a stand. Watch `env/hold_frac` in TensorBoard; that is the metric that means anything here.
- Train laps (fine-tune from the sprint policy; exports `Assets/Policies/athlete_track.onnx`):
  `.venv/Scripts/python.exe train_run.py --task track --resume checkpoints/run_to_target/latest.pt --iters 2300 --target-speed 4.0`
- Evaluate a **sprint** policy: `.venv/Scripts/python.exe eval_100m.py --runs 5`
- Evaluate a **lap** policy: `.venv/Scripts/python.exe eval_lap.py --ckpt checkpoints/run_track/latest.pt`
  Use the right one. `eval_100m.py` runs a straight line at a target 108 m away, which holds the
  observation's `min(dist, 10) / 10` slot at 1.0 for the first 98 m; a `run_track` policy has never
  seen that, because its carrot sits 6 m ahead and that slot reads 0.6 for the whole of training.
  Ranking lap policies on the sprint is measuring the wrong task, and it is how a policy that could
  not hold the line once looked 41% faster. `eval_lap.py` runs the real one -- same carrot, same
  5.3 m deck, actor mean rather than sampled actions, as Unity executes it -- and reports the number
  that decides whether "faster" is better: how many athletes finish clean.
- Export any checkpoint to ONNX (not just the newest):
  `.venv/Scripts/python.exe export_checkpoint.py --ckpt <file.pt> --out <file.onnx>`
  Needed because a run trained against a limit does not improve monotonically: the adaptive speed
  curriculum hunts around the fastest sustainable pace, so consecutive checkpoints alternate between
  clean and fall-heavy and the last iteration is not reliably the best one.
- **After pulling the broadcast layer added 2026-09-12, re-run the scene builders once.** The new
  components (`EffortMeter`, `StressSkeleton`, `SkidMarks`, `DramaMeter`, `CameraShake`, `Commentary`,
  `SpeechSynth`, the impulse listeners on the shot cameras) are wired by the editor builders, so a
  scene saved before them has none of it. Run `PoDecath/Build Race Scenes` and
  `PoDecath/Build Long Jump Scene`; they re-bake the effects bank on the way past, which is where the
  two new materials (`Fx_Stress`, `Fx_Skid`) come from.
- Unity: menu `PoDecath/Build Rooftop Scene`, then play `Assets/Scenes/Rooftop.unity`.
- Audio: menu `PoDecath/Bake Audio Clips` synthesises the whole sound set into `Assets/Audio/` and points
  `AudioBank.asset` at it. The scene builders run it themselves, so this is only needed to re-bake by hand.
- Surfaces, effects, look and UI panel: `PoDecath/Bake Surfaces`, `Bake Effects`, `Bake Look`, `Bake UI Panel`.
  Like the audio, the scene builders call these themselves; the menu items are for re-baking after a recipe
  changes. Every one of them is deterministic, so a re-bake does not churn the repository.
- Measure, do not squint: `PoDecath/Sweep All Scenes` plays every scene in the build list and writes
  `training/logs/scene_sweep.json` (errors, rigs bound, policies loaded, draw calls) plus a PNG each;
  `PoDecath/Probe Get-Up Transfer` scores a recovery policy from a supine start into
  `training/logs/getup_transfer.json`. Both run through the Unity CLI
  (`unity command menu --path "..."`), so they work headless against the open editor.
- Building LODs: `blender-launcher --background --python training/tools/whitehouse_lods.py -- Assets/Models/WhiteHouse.glb Assets/Models`
  (8 s; writes `WhiteHouse_LOD1.glb` and `WhiteHouse_LOD2.glb` with material names and no textures; the scene
  builders attach them). Re-run after every glb re-export from Blender.
- Lighting: `PoDecath/Bake Lighting (lightmaps + probes)` bakes the four rooftop scenes (minutes; log in
  `training/logs/lighting_bake.log`). Every scene rebuild throws the lightmaps away, so bake last.
- Race logs: every finished race writes `training/logs/races/race_<time>.json` in the editor and
  `Android/data/com.podecath.game/files/races/` on a phone: device, tier, mean and 1% low FPS, worst frame,
  draw calls, triangles, battery before and after, and the results. Put two side by side to compare builds.
- Diagnostics: F3 in any scene opens the telemetry overlay (frame graph, draw calls, GC, memory, athletes,
  audio voices, crowd mood). It is the thing to open before believing any performance claim.
- Long jump: menu `PoDecath/Build Long Jump Scene` (also rebuilds `MAIN.unity`), then play
  `Assets/Scenes/RooftopLongJump.unity` or go through the setup menu.
- Setup menu (`MAIN.unity`, build index 0 -- the scene to press Play on): a 100 M / 400 M / 1500 M / HURDLES / LONG JUMP picker, then a counter per athlete definition
  up to 16 in total. The lap is 100.1 m, so it is the game's 100 m; the 20 m dash
  on the straight (`Rooftop.unity`) is a development scene and is not offered on the menu. Every loop event
  is the same `RooftopRace.unity` and the same `LapEvent`: the picker writes the lap count and the hurdles
  flag into `SessionSettings` on its way out, and `LapEvent.Awake` turns the lap count into the race
  distance and the clock (60 s per lap, capped at 600 s). The infield is
  34.7 x 12.3 m, so the runway is 17.9 m and the pit 8 m (regulation 40 m + 9 m does not fit); the event
  runs 2.4 m south of the loop centre line to clear the White House flagpole.

## How fast the athletes can go, and why

The RL athletes are capped by their own reward, and then by the track. Both matter, and they bind in
different places.

The reward first: `r_track` is a Gaussian centred on `target_speed` and `r_prog` clamps to it, so
exceeding the target pays nothing and costs something. At the shipped default of 3.5 m/s (4.0 for
track) an athlete peaking near 4.5 m/s has done exactly what was asked. `--target-speed-final` with
`--speed-adaptive` raises the ask while the athlete keeps its feet.

Then the geometry, which is the binding constraint for the lap. The loop is 44.8 m of straight and
55.3 m of bend at 8.8 m radius, so **55% of a lap is cornering**:

| speed | lateral acceleration | measured outcome |
|---|---|---|
| 4.08 m/s | 1.89 m/s2 (0.19 g) | shipped policy, 24.54 s lap, 100% of athletes clean |
| 5.50 m/s | 3.44 m/s2 (0.35 g) | fall rate 0.6 - 1.0 |
| 8.33 m/s | 7.89 m/s2 (0.80 g) | straight-line sprint peak; not available on an 8.8 m bend |

So the athlete really can sprint at 8+ m/s -- `eval_100m.py` measures that, and on a straight it is
real -- and really cannot corner at 8.8 m radius much above 5 m/s without learning to bank into the
turn. Raising `target_speed` buys speed on 45% of the lap and falls on the other 55%. Training runs
that pushed past ~5 m/s ended with 0% of athletes completing a clean lap.

Making the lap meaningfully faster therefore wants one of: a larger bend radius (a track change, not a
policy change), a reward that pays for leaning into the turn, or an event on the straight where the
sprint speed is usable. It is not a matter of training the current task harder.

## Bot roster rules

Every event has a reference RL bot and zero or more custom bots; the heuristic-coded sprinter that used
to be required was removed on 2026-09-14 (owner decision), code, asset and probe alike. Roster entries
are `AthleteDefinition` assets under `Assets/Athletes/`. Athletes keep the textures their model was
imported with and are **not** tinted (house rule, owner decision 2026-09-05, replacing the earlier
RED/GREEN/custom colour scheme); the colour on a definition survives only in the UI, where it is the
menu swatch, the results row and the trail ribbon that tell a field apart on the deck.

The field is picked on the setup menu: one row per roster entry, a stepper either side of a count, 1 to
16 runners in total, with ONE EACH and NONE for setting the whole list at once. A count of 0 leaves that
athlete out. The grid is filled round-robin so each starting row gets a mix rather than one policy per
row. The menu opens with one of everybody.

### Adding an athlete

1. Drop the rigged model — `.glb` or `.fbx` — into `Assets/Models/Characters/`.
2. Run `PoDecath/Rebuild Athlete Roster`, then `PoDecath/Build Race Scenes` and
   `PoDecath/Build Long Jump Scene` so the scenes and the menu pick it up.

That is the whole procedure; nothing needs typing by hand. `SkeletonMapper` reads the twelve MJCF bodies
off the skeleton's shape rather than off its bone names, which is what lets one rule cover Mixamo
(`LeftLeg`), AccuRig (`CC_Base_L_Calf`) and an export that numbers its bones `bone_27`. It also works
out which way the model faces. `SkinBinder` then sizes the skin to the physics rig as it binds, by
fitting every mapped bone at once, so models authored at different heights — 1.84 m for Matt, 1.15 m for
Trump — all drive the same body without anyone editing a scale.

Two things worth knowing when it goes wrong. The inferred map is written into the definition as plain
bone names and is **only ever inferred once**, so a correction by hand survives the next rebuild; delete
the `boneMap` list to have it inferred again. And an FBX does not arrive dressed: Unity imports the mesh
and leaves the embedded textures inside the file, so the roster builder extracts them into
`<model>_Textures/`, binds them to a real material in `<model>_Materials/` and remaps the importer onto
it. Without that step the athlete races as flat grey and nothing in the import log says why.

The seven models on the roster today are Matt Avaturn, Grandma, Grandpa, Nick, Nick Doggy, Trump and
Zombie Accurig, all twelve bodies bound on each, alongside the reference Matt RL. (The plain Matt character
and the heuristic sprinter were removed on 2026-09-14, owner decision.) They all run the same lap policy — the skeleton and the skin are what differ, not the brain —
so a race between them is a beauty contest, not a comparison of policies. Three of them are heavy:
Trump, the zombie and the doggy are around 50k triangles apiece against roughly 10k for the rest, so a
full field of sixteen is worth re-checking with `PoDecath/Sweep All Scenes` rather than assuming the
figures in this file still hold.
