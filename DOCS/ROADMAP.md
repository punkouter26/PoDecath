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
| 5 | 400 m | `run_track` (carrot on the loop centre line, `athlete_track.onnx`) | `LapEvent` + `TrackFollower`, `laps` = 4 from the event picker | playable; 400 m in 44.6 s by the RED bot |
| 6 | Hurdles | clearance not trained; athletes run into them | `HurdleSet` + `Hurdle` on the loop, picked as HURDLES | playable, nobody clears one yet |
| 7 | Discus | throw (placeholder) | placeholder | placeholder |
| 8 | Pole vault | placeholder | placeholder | placeholder |
| 9 | Javelin | throw (placeholder) | placeholder | placeholder |
| 10 | 1500 m | `run_track`, 15 laps | `LapEvent`, `laps` = 15 from the event picker | playable; a fall now hands the body to the get-up policy rather than ending the race, and is only a DNF if the recovery gives up |

Scoring: IAAF decathlon tables to be added as a `ScoringTable` ScriptableObject.

## Athlete behaviours

| Behaviour | Training env | Notes |
|---|---|---|
| Run to target | `training/envs/run_to_target.py` | done, Phase 1 |
| Balance / get up after a fall | `training/envs/get_up.py` | real env, trained with `--task getup`: fallen-pose resets on a widening tilt curriculum, reward on uprightness then height then a one-second hold, timeout-only termination. Keeps the run-to-target observation contract with the command zeroed, so `athlete_getup.onnx` drops into `PolicyRunner` unchanged and `RecoveryController` switches to it on a fall |
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

- **Get-up transfers poorly from MuJoCo to PhysX, and this is the top open item.** The policy is good in
  the trainer — 2400 iterations reach stood 1.00 / stand 0.78 / hold 0.59 at full difficulty — and the Unity
  plumbing around it is verified in play mode: a fallen athlete hands control to `athlete_getup.onnx`,
  `RecoveryController` runs its state machine, and a failed attempt becomes a DNF without stalling the race.
  But measured in the editor, the policy takes uprightness from 0.03 to roughly 0.35 and falls back; it
  never completes the stand.

  Getting up is the worst possible case for sim-to-sim transfer, because the entire motion is ground
  contact: friction, contact stiffness and how a many-limbed body resting on a surface is solved. The
  running policies transfer because a runner is airborne or on one foot most of the time and barely
  touches the regime where the two engines disagree. Things to try, roughly in order of expected value:
  domain randomisation over friction and contact parameters during training (Isaac's transfer only worked
  once randomisation was added, per `DOCS/COMPARISON.md`); matching PhysX's solver iteration count and the
  foot `PhysicsMaterial` to the MJCF's contact parameters; and checking the articulation drive gains hold
  up under the sustained high torque a get-up needs, which is a very different load from running.
- **No athlete can clear a hurdle.** The hurdles event is playable and physical — 0.762 m bars on 9 kg
  frames that topple when hit, knocks counted per runner and shown on the results board — but every
  policy runs straight into them. Measured over a 3-strong RL field before the get-up policy existed: all
  three down inside 25 m, 3 of 7 hurdles knocked over — and every one of those was a DNF. With recovery
  wired, a runner that goes down over a hurdle can now get up and carry on, which turns the event from
  unfinishable into merely very slow. A take-off policy trained on this course is still the real fix; the
  course is there to train against.
- Audio is entirely synthesised (`PoDecath/Bake Audio Clips` -> `Assets/Audio/`). It is a complete,
  reproducible placeholder set, not a sample library; every clip can be replaced on the `AudioBank`.
- Long jump take-off is scripted: `LongJumpEvent.takeoffRise` adds one vertical velocity to the base at the
  board. Run-up, foot plants (take-off point and fouls), flight and the landing mark are all simulated. The
  sprint policy reaches only ~4.5 m/s on the 17.9 m runway, so its marks are ~2-3 m against the RED bot's
  ~6 m at 9 m/s; a take-off policy trained on the runway is the real fix, not a bigger impulse.
- Kart deck columns are decorative; no kart physics.
- Mobile build not yet profiled with the 25 MB White House mesh (consider LODs / mesh decimation).
