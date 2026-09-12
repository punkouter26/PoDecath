# Roadmap and placeholders

Phase 1 delivers the rooftop track and a 100 m dash with a trained running policy. Everything below is
scaffolded as a placeholder so later phases plug into the same athlete, event and training pipeline.

## Events (Decathlon order)

| # | Event | Training task | Unity controller | Status |
|---|---|---|---|---|
| 1 | 100 m dash | `run_to_target` | `RaceEvent` | Phase 1 |
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

- ~~Get-up transfers poorly from MuJoCo to PhysX~~ — **closed 2026-09-06; it always transferred.** Kept
  here because the wrong diagnosis stood for weeks and the shape of the mistake is worth remembering.
  Measured with `PoDecath/Probe Get-Up Transfer` from a flat supine start (upright 0.051), the shipped
  `athlete_getup.onnx` reaches **peak uprightness 0.947 and holds the stand 7.44 s of 8**. The fault was in
  the watcher, not the policy: `RecoveryController.FloorY` cast a ray down from above the pelvis with mask
  `~0`, hit the athlete's own chest collider, drove `heightFrac` negative, and so `standing` — the only exit
  from `Recovering` — could never be true. Every athlete that stood up was run to `giveUpSeconds` and booked
  a DNF anyway. One layer mask; before/after reads **recoveries 0 -> 3**. See `DOCS/README.md` and
  `AGENTS.md` for the full account. The lesson is the general one: before blaming a policy for a behaviour,
  confirm the thing measuring it can see what it claims to measure.
- **The RED bot falls on the Rooftop dash, and four plausible causes have been ruled out.** Measured by
  `PoDecath/Sweep All Scenes`: in `Rooftop.unity` the heuristic athlete ends every 6 s run on its back —
  upright ≈ **-0.035**, **1.31 m** travelled, **0.22 m/s** — while the two RL athletes in the same scene
  are fine (upright 0.97+, 8.5–11 m). The same `Heuristic_Sprinter` definition runs clean in
  `RooftopLongJump` (upright 1.0, 15.38 m at 2.56 m/s), so it is specific to `Rooftop.unity` /
  `RaceEvent`, not to the bot.

  What it is **not**, each disproved by measurement rather than argument — and note the distance and
  speed came back *identical to two decimals* every time, which is the tell that the change never
  reached the running code:
  1. *Not the countdown hold.* `RaceEvent` pins only `a.IsRL` during the countdown; pinning the
     heuristic too changed nothing.
  2. *Not the speed ask.* Dropping `topSpeed` from 9.2 to 4.0 changed nothing.
  3. *Not the spawn height.* The heuristic branch placed the base at deck level with no `spawnHeight`
     lift, unlike the RL branch. Fixed; changed nothing.
  4. *Not the dead reset branch.* `Athlete.IsRL` is `rig != null`, which became true for the heuristic
     bot when it gained a body, so `ResetHeuristic` had been unreachable. Fixed (the branch is now
     ordered heuristic-first, verified no regression in the long jump); changed nothing.

  Fixes 3 and 4 are real bugs and are committed on their own merits. The fall is still open, and it is
  upstream of `ResetAthlete`. Two things worth knowing before the next attempt. `PoDecath/Probe
  Heuristic Gait` **cannot currently be trusted in this scene**: run there with `driver: "none"` — no
  controller at all, drives holding defaults — the body still goes over at 2.09 s, and with
  `driver: "policy"` at 0.74 s, even though the policy athletes are upright in the real scene. By the
  probe's own documented criterion that means it is setting the body down badly, so fix the probe's
  placement before believing any gait number it reports. And the fall looks insensitive to controller
  input entirely, which points at the body's state at spawn — `AthleteSpawner`/`SpawnBody`/`MjcfImporter`
  — rather than at `HeuristicGait`.
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
- **Commentary has no voice on Mac, Linux or iOS.** Android speaks through the platform
  `TextToSpeech` and Windows through the system synthesiser (shelled out per line, which is not how a
  shipping game should talk); everywhere else the captions carry it alone. Fixing it properly means
  bundling a synthesiser — Piper (MIT) or sherpa-onnx (Apache-2.0, and it runs ONNX, which this
  project already ships a runtime for).
- **The broadcast layer's thresholds are first guesses and want a watched race.** `DramaMeter`'s
  lateral-acceleration scale (3.44 m/s2) and close-gap (2.5 m) come from measurements this project
  already made, but `BroadcastDirector.anticipateRisk` (0.72), `RaceVfx.referenceImpulse` (45 N s) and
  `EffortMeter.fatigueJoules` (16000) do not: nothing has measured what a real hurdle knock or a real
  400 m actually produces. Watch one race and re-tune, rather than trusting the numbers in the file.
- **None of it has been profiled.** The effort meters, the stress meshes, the skid quads and the camera
  shake are all cheap by design and all untested against a frame budget that is already 10x over on
  the building mesh. Re-run `PoDecath/Sweep All Scenes` and compare before believing otherwise.
- Mobile build not yet profiled with the 25 MB White House mesh (consider LODs / mesh decimation).
