# Roadmap and placeholders

Phase 1 delivers the rooftop track and a 100 m dash with a trained running policy. Everything below is
scaffolded as a placeholder so later phases plug into the same athlete, event and training pipeline.

## Events (Decathlon order)

| # | Event | Training task | Unity controller | Status |
|---|---|---|---|---|
| 1 | 100 m dash | `run_to_target` | `DashEvent` | Phase 1 |
| 2 | Long jump | run-up = `run_to_target` with a carrot; take-off scripted (one vertical impulse), flight and landing are PhysX; a real take-off policy is still to train | `LongJumpEvent` + `LongJumpPit` on the infield deck (`LongJumpBuilder`), menu `PoDecath/Build Long Jump Scene` | playable, 3 rounds each |
| 3 | Shot put | throw (placeholder) | placeholder | placeholder |
| 4 | High jump | run-up + jump-over-bar (placeholder) | placeholder | placeholder |
| 5 | 400 m | `run_track` (carrot on the loop centre line, `athlete_track.onnx`) | `LapEvent` + `TrackFollower` (set `laps` = 4 for 400 m) | 1 lap verified |
| 6 | 110 m hurdles | run + hurdle clearance (placeholder) | placeholder | placeholder |
| 7 | Discus | throw (placeholder) | placeholder | placeholder |
| 8 | Pole vault | placeholder | placeholder | placeholder |
| 9 | Javelin | throw (placeholder) | placeholder | placeholder |
| 10 | 1500 m | endurance loop | placeholder | placeholder |

Scoring: IAAF decathlon tables to be added as a `ScoringTable` ScriptableObject.

## Athlete behaviours

| Behaviour | Training env | Notes |
|---|---|---|
| Run to target | `training/envs/run_to_target.py` | done, Phase 1 |
| Balance / get up after a fall | `training/envs/get_up.py` | placeholder env stub; reuse PPO + export path |
| Kart driving (rooftop track) | placeholder | karts are not part of Phase 1 |

## Bots roster (house rules)

- Heuristic bot: RED, `HeuristicRunner` (kinematic pace profile).
- Reference RL bot: GREEN, `athlete_run.onnx`.
- Custom RL bots: owner-supplied textures and skinned meshes via `AthleteDefinition` assets.

## Next steps (agreed order)

1. Add more humanoids: one `AthleteDefinition` per rigged glb (skin, bone map, own policy), each trained
   through `rig_to_mjcf.py` + `train_run.py --task track`.
2. Hurdles, high jump: new training tasks + event controllers (placeholders below). Long jump has its
   event, pit and cameras; what it still needs is a trained take-off (see Known gaps).
3. Mobile build: decimate/LOD the White House mesh in Blender (target < 200k triangles on screen),
   then profile on device at 60 FPS.

## Known gaps

- Get-up policy not trained yet; a fallen RL athlete is marked DNF and the race restarts.
- Long jump take-off is scripted: `LongJumpEvent.takeoffRise` adds one vertical velocity to the base at the
  board. Run-up, foot plants (take-off point and fouls), flight and the landing mark are all simulated. The
  sprint policy reaches only ~4.5 m/s on the 17.9 m runway, so its marks are ~2-3 m against the RED bot's
  ~6 m at 9 m/s; a take-off policy trained on the runway is the real fix, not a bigger impulse.
- Kart deck columns are decorative; no kart physics.
- Mobile build not yet profiled with the 25 MB White House mesh (consider LODs / mesh decimation).
