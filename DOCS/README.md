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
- Lap scene: `Assets/Scenes/RooftopLap.unity` from menu `PoDecath/Build Rooftop Lap Scene`. The RED pacer
  is hidden there by default (`AthleteSpawner.includeHeuristic`).
- Verified: final lap policy (`athlete_track.onnx`, 2300 iterations) laps in 24.2 s, within 1 m of the
  centre line, no falls.
- Trainer comparison: the same body was also trained in **Isaac Lab** (`training/isaac/`, separate venv). Its
  policy is the YELLOW athlete "Matt Isaac"; results and lessons in `DOCS/COMPARISON.md`.
- Mobile performance (Task 8): 522 static renderers, ~837k triangles, ~700 batches in the Editor. Static
  batching is on and the target is 60 FPS, but the 122 MB building mesh needs decimation/LODs from Blender
  before a phone build; profile on device.

## Phase 1 scope (current)

| Piece | Where | Status |
|---|---|---|
| Athlete skinned mesh (Matt, Mixamo rig) | `Assets/Models/Athlete_Matt.glb` | provided by owner |
| Physics rig generated from the rig bones (21 DoF) | `training/rig_to_mjcf.py` -> `training/models/athlete.xml` | done |
| Run-to-target training (PPO on MuJoCo Warp, 4096 envs) | `training/train_run.py`, `training/envs/run_to_target.py` | runs, exports `Assets/Policies/athlete_run.onnx` |
| 100 m evaluation in MuJoCo | `training/eval_100m.py` | done |
| MJCF -> ArticulationBody importer + skin binding | `MjcfImporter.cs`, `SkinBinder.cs` | done |
| Rooftop kart track (ProBuilder) | `KartTrackBuilder.cs` via menu `PoDecath/Build Rooftop Scene` | done |
| 100 m dash event with RED heuristic bot and GREEN RL bot | `RaceEvent.cs`, `AthleteSpawner.cs`, `HeuristicRunner.cs` | done |
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
| Other events (high jump, throws, ...) | `DOCS/ROADMAP.md` | placeholders |

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
Assets/Policies                 ONNX checkpoints + PolicyLibrary (auto-refreshed)
training/                       Python: MJCF generation, MuJoCo Warp env, PPO, eval, TensorBoard logs
DOCS/                           this summary and the roadmap
```

## Running things

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
- Diagnostics: F3 in any scene opens the telemetry overlay (frame graph, draw calls, GC, memory, athletes,
  audio voices, crowd mood). It is the thing to open before believing any performance claim.
- Long jump: menu `PoDecath/Build Long Jump Scene` (also rebuilds `MAIN.unity`), then play
  `Assets/Scenes/RooftopLongJump.unity` or go through the setup menu.
- Setup menu (`MAIN.unity`, build index 0 -- the scene to press Play on): a 100 M / 400 M / 1500 M / HURDLES / LONG JUMP picker, then a counter per athlete definition
  including the RED heuristic bot, up to 16 in total. The lap is 100.1 m, so it is the game's 100 m; the 20 m dash
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

Heuristic bots are RED, the reference RL bot is GREEN, custom RL bots use owner-supplied textures and
skinned meshes. Roster entries are `AthleteDefinition` assets under `Assets/Athletes/`.
