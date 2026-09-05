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
| 100 m dash event with RED heuristic bot and GREEN RL bot | `DashEvent.cs`, `AthleteSpawner.cs`, `HeuristicRunner.cs` | done |
| Lap race around the roof loop (carrot follower + lap policy) | `TrackPath.cs`, `TrackFollower.cs`, `LapEvent.cs`, `training/envs/run_track.py`, menu `PoDecath/Build Rooftop Lap Scene` | done: 100 m lap in 25 s, no falls |
| Balance / get-up policy | `training/envs/get_up.py` | placeholder |
| Long jump on an infield deck inside the loop (ProBuilder runway, board, recessed sand pit; sequential attempts, 3 rounds, best mark; broadcast cuts + results modal; picked from the setup menu) | `LongJumpBuilder.cs`, `LongJumpPit.cs`, `LongJumpEvent.cs`, menu `PoDecath/Build Long Jump Scene` | done: scene `Assets/Scenes/RooftopLongJump.unity`; take-off impulse scripted until a jump policy exists |
| Other events (high jump, hurdles, ...) | `DOCS/ROADMAP.md` | placeholders |

## Folder map

```
Assets/Scripts/Runtime/Sim      physics, policy runner, importer, events, athletes
Assets/Scripts/Runtime/UI       portrait HUD and menu
Assets/Scripts/Editor           scene/prefab builders (PoDecath menu)
Assets/Models                   WhiteHouse.glb, Athlete_Matt.glb, athlete.xml (copied from training)
Assets/Policies                 ONNX checkpoints + PolicyLibrary (auto-refreshed)
training/                       Python: MJCF generation, MuJoCo Warp env, PPO, eval, TensorBoard logs
DOCS/                           this summary and the roadmap
```

## Running things

- Train: `cd training && .venv/Scripts/python.exe train_run.py --num-envs 4096 --iters 1500`
  (TensorBoard opens on http://localhost:6006; stale runs are cleared first).
- Train laps (fine-tune from the sprint policy; exports `Assets/Policies/athlete_track.onnx`):
  `.venv/Scripts/python.exe train_run.py --task track --resume checkpoints/run_to_target/latest.pt --iters 2300 --target-speed 4.0`
- Evaluate: `.venv/Scripts/python.exe eval_100m.py --runs 5`
- Unity: menu `PoDecath/Build Rooftop Scene`, then play `Assets/Scenes/Rooftop.unity`.
- Long jump: menu `PoDecath/Build Long Jump Scene` (also rebuilds `RaceSetup.unity` with a LAP RACE / LONG JUMP
  picker), then play `Assets/Scenes/RooftopLongJump.unity` or go through the setup menu. The infield is
  34.7 x 12.3 m, so the runway is 17.9 m and the pit 8 m (regulation 40 m + 9 m does not fit); the event
  runs 2.4 m south of the loop centre line to clear the White House flagpole.

## Bot roster rules

Heuristic bots are RED, the reference RL bot is GREEN, custom RL bots use owner-supplied textures and
skinned meshes. Roster entries are `AthleteDefinition` assets under `Assets/Athletes/`.
