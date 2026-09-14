# Training tuning applied 2026-09-13

All ten items are in the working tree. **No training has been run** — the only run was a three-iteration
smoke test to prove the loop works end to end, and the policy it exported was reverted.

Host: RTX 5070 Ti Laptop (12 GB, sm_120), Core Ultra 9 275HX, 32 GB RAM.
Stack: torch 2.11.0+cu128, warp 1.17.0, mujoco 3.13.0, mujoco_warp @ main.

---

## Three things the pre-work audit got wrong

Recorded because each one would otherwise still be believed.

1. **`rig_to_mjcf.py` had not drifted from the shipped XML.** Regenerating reproduced
   `athlete.xml` byte for byte. The tuned gains are not hard-coded in the script's `GAINS` table --
   they come from `rigs/matt.json` via `rig.get("gains", GAINS)`, which is why the script's own
   defaults look stale and are not. The generator was the source of truth it claimed to be.
2. **`nconmax` / `njmax` are per world, not totals.** `mujoco_warp.put_data` documents "Number of
   contacts to allocate **per world**"; `naconmax` is the all-worlds figure. The old 24/96 was 24
   contacts per athlete, which is ample for two feet on a plane. No contacts were being dropped and
   the training history is not suspect.
3. **Unity was not halving the action scale.** `Assets/Policies/Athlete_PolicyConfig.asset` does carry
   `actionScale: 0.25`, but it is a dead artefact -- it still holds the Unitree Go2 quadruped joint
   list (`FL_hip_joint`, ...) that `ApplyGo2Defaults()` seeded it with, and the athlete path never
   reads it. `AthleteSpawner.SpawnBody` builds a fresh `PolicyConfig` per athlete from
   `athlete_policy_config.json`, where `action_scale` was already 0.5, matching the trainer. Two
   *other* trainer/Unity constants were genuinely stale; see item 7.

A fourth, found while applying: **the rig already declared the right mass.** `rigs/matt.json` carries
`"mass_kg": 75.0` and `"height_m": 1.74`. The generator ignored the field completely.

---

## 1. Self-collision — the athlete's limbs passed through each other

`athlete.xml` defaulted every geom to `contype="1" conaffinity="0"`; only the floor had
`conaffinity="1"`. A contact needs `(contype_a & conaffinity_b) || (contype_b & conaffinity_a)`, which
for any two body geoms is `(1&0)|(1&0) = 0`.

**Measured before:** both hips driven fully inward until the thigh capsules overlapped (centres 6.8 cm
apart, 6 cm radius each) reported `ncon = 0`.
**Measured after:** the same pose reports `ncon = 1`, `thigh_l_geom <-> thigh_r_geom`.

Geoms now default to `conaffinity="1"`, with a generated `<contact><exclude>` block for the eleven body
pairs that are adjacent across a joint and overlap by construction. That was the documented reason
self-collision was switched off in the first place (AGENTS.md: pelvis/thigh capsules "force the hips to
their abduction limits") -- an argument for excluding those pairs, not all of them.

**This surfaced a real incompatibility.** With the two foot boxes now a collidable pair, `put_model`
refuses the model outright: *"geom pair (foot_l_geom, foot_r_geom) has non-zero margin with MULTICCD
enabled"*. The default `margin="0.001"` is now `margin="0"`. That would have failed at training launch.

## 2. Mass — 49.8 kg on a 1.74 m frame

Every geom ran at `density="1000"` and `rig["mass_kg"]` was ignored, giving a BMI of 16.5. The generator
now writes an explicit per-geom `mass` from Winter/Dempster segment fractions of the declared body mass,
so MuJoCo still derives each inertia tensor from the real shape.

**Measured after: 75.00 kg**, against the 75.0 the rig asked for.

| segment | before | after |
|---------|--------|-------|
| pelvis | 9.45 | 10.65 |
| torso + head | 15.04 | 32.70 |
| thigh (each) | 5.53 | 7.50 |
| shin (each) | 2.91 | 3.49 |
| foot (each) | 1.60 | 1.09 |
| **total** | **49.81** | **75.00** |

The foot went *down*: it was 55% heavy relative to the rest, which distorts swing-leg dynamics more
than the total mass does.

The collision capsules are unchanged, so the trunk is still a 17 cm cylinder where a real one is ~30 cm.
Mass and centre of mass are now right; trunk roll inertia is still low. Widening the trunk is a
geometry change with its own contact consequences and is **not** done here.

## 3. Gait reward — twelve reward terms, none about the feet

Added to `run_to_target.py`, inherited by both other tasks: flight time paid on footfall (not on
hanging in the air), foot-slip, swing clearance, stance width, double-support penalty while moving,
contralateral arm swing, cost of transport from the exact actuator torques, and a joint-speed penalty
against human sprint peaks (house rule 15).

Every one is bounded. The first version was not, and a falling ragdoll scored **-2669** on the track
task because the unclamped joint-speed term alone reached three thousand. After clamping: **+0.63**.

Three gait numbers now print each iteration and go to TensorBoard: `duty_factor` (fraction of time a
foot is down -- human walking ~0.6, running ~0.35), `air_time` (seconds of flight per footfall) and
`foot_slip` (m/s of sliding under a loaded foot). `v_toward` alone cannot tell a run from a fast
shuffle; these can.

## 4. Joints — ankles at 40% of human strength, bending the wrong way

| joint | force before | after | human peak |
|-------|--------------|-------|------------|
| ankle_y | 100 | 220 | 200-280 |
| ankle_x | 100 | 60 | 40-60 |
| knee | 220 | 260 | 250-300 |
| hip_y | 220 | 240 | 200-300 |
| abdomen | 150 | 170 | 150-200 |

Ankle `kp` went 80 -> 250 with the force limit. Raising the limit alone would have changed nothing: at
kp 80 the actuator could not command more than ~56 N m whatever the limit said. This is the change most
likely to need retuning, since it is the one that most changes what the body can do.

`ankle_y` range was `-45 45`; a human ankle is not symmetric (~20 deg dorsiflexion, ~50 deg
plantarflexion) and it is now `-20 50`. Sign convention verified: rotating the foot's +x toe direction
about +y by a negative angle lifts it, so negative is dorsiflexion.

**`hip_x` was left at +-40 on purpose**, reversing what the audit proposed. Human abduction (~45 deg)
and adduction (~25 deg) are asymmetric, but `RANGES_DEG` is not mirrored for the legs, so an asymmetric
entry would be right on one side and backwards on the other. Adduction past the real limit is now
stopped by the thighs actually touching -- the physically correct mechanism, and it only exists because
of item 1.

Armature went from a uniform 0.01 -- far below the limb inertia any of these joints swings -- to 0.02-0.10
per group.

## 5, 6, 7. Throughput

**Measured, same host, 4096 environments, 120 steps after warm-up:**

| task | before | after | speedup |
|------|--------|-------|---------|
| run-to-target | 86,649 steps/s | 283,502 steps/s | **3.27x** |
| track lap | 64,759 steps/s | 160,716 steps/s | **2.48x** |

Three changes:

- **`run_track.centerline` is branchless.** It selected each of the four track segments with a boolean
  mask (`s[m1]`), which is `masked_select`: the output size is unknown until the mask is counted on the
  device, so each line stalled the pipeline. It ran three times per step, plus once over a 25x-expanded
  tensor in `project`. With `newly_done.any()` -- another device-to-host read -- that was about a dozen
  stalls per step in a file whose sibling advertises a "sync-free step path". All now `torch.where`.
- **CUDA-graph capture** of the decimation loop and the post-reset `forward`. `mjw.step` dispatches
  dozens of small kernels and the launch overhead dominates at these world counts.
- **TF32** enabled in `ppo.py`; the actor and critic are plain MLPs.

The same benchmark with capture disabled runs at **32,952 steps/s** -- *slower than the original*. The
new physics (self-collision, solver 6->10 iterations, contact budget 24->48) costs roughly 2.6x more per
step. Graph capture is what pays for it, and then some. `--no-cuda-graph` exists for debugging.

**Environment-count sweep** (run-to-target, capture on):

| envs | steps/s | VRAM |
|------|---------|------|
| 2048 | 166,883 | 1.75 GB |
| 4096 | 250,941 | 2.21 GB |
| 8192 | 327,713 | 3.07 GB |
| 16384 | 376,152 | 4.83 GB |
| 24576 | 376,124 | 6.54 GB |
| 32768 | 352,222 | 8.38 GB |

Throughput plateaus near 16k and VRAM is never the limit. The default is now **8192, not 16384**: every
extra environment also enlarges the PPO batch without buying more gradient steps. `--minibatches` now
defaults to holding the minibatch near the 24k samples that 4096 envs x 24 steps / 4 used to give.

**Action range.** Actions were clamped at +-5, which with `action_scale` 0.5 requests +-2.5 rad on joints
whose entire range is under 2.6 rad and mostly under 1.5. Most of the action space landed on the
`ctrlrange` clamp, where the gradient is zero, and the policy was rewarded for saturating. Now +-3.

**The two real Unity mismatches** (not the one the audit claimed): `AthleteSpawner` hard-coded
`cfg.actionClip = 5f` to match the old trainer clamp, and `spawnHeight = keyframeRootHeight + 0.02f` to
match the old reset drop. Both now come from the generated policy config (`action_clip`,
`spawn_clearance_m`) so they cannot drift again.

## 8. Episodes no longer reset in lockstep

`GetUpEnv` already had `_stagger()`, with a comment explaining that lockstep resets make every metric
alias against the episode period -- "which is exactly how a run came to look like it was standing 22% of
the time when it was not standing at all". It was never applied to the base environment, which is where
`train_run.py` documents the same damage: iterations reporting a fall rate of 0.0 that means "no data",
with the adaptive speed curriculum gated on that number. Lifted into `RunToTargetEnv.reset`.

## 9. Observations — 75 -> 78 floats

Added `foot_contact(2)` and `base_height(1)`. A legged policy that cannot see which foot is down has to
infer stance from joint angles, and item 3's reward is built entirely on stance and swing.

Dropped, in the track task only: `r_reach`. The carrot sits a fixed 6 m ahead, so the base task's
"arrived" test (under 0.6 m) can never be true and its 5.0 bonus -- nominally the largest single term in
the reward -- had never paid out once in this task's history.

The third command channel was a constant for the same reason: `min(dist, 10) / 10` is 0.6 forever. It
now carries signed lateral offset from the centre line, normalised by the half-deck -- the one quantity
the athlete is penalised and terminated on and previously could not observe.

Unity mirrors all of it: `PolicyConfig.includeFootContact` / `includeBaseHeight`, filled by
`ObservationBuilder` from the `FootContactSensor` components the rig already had, and enabled
automatically from the `observation` list in the policy JSON.

## 10. Reset drop and solver headroom

The `stand` keyframe put the soles at **z = +0.010** and `_reset_states` added another 0.020, so every
episode opened with a 3 cm free fall and an impact the policy did not cause. Sole height in the stand
pose works out to exactly `(keyframe z - pelvis z)`, so the keyframe offset is now 0.002 and the reset
adds 0.002. **Measured after: sole at 0.0020 m.**

Solver `iterations` 6 -> 10, `ls_iterations` 8 -> 10, `nconmax` 24 -> 48, `njmax` 96 -> 192 — all needed
once the body collides with itself.

---

## Unity side

`Physics.IgnoreLayerCollision(Creature, Creature, true)` is gone -- it would have undone item 1 entirely.
`MjcfImporter` now reads the MJCF's own `<contact><exclude>` list and applies `Physics.IgnoreCollision`
per collider pair, so the two runtimes exclude exactly the same pairs from one source.

That layer-wide ignore was also doing a second job: keeping two athletes from colliding with each other.
`AthleteSpawner.IgnoreBetweenAthletes` now does that explicitly, at spawn. Athletes still pass through
one another, deliberately -- training only ever sees one body on an empty plane, so a policy has no idea
what to do when shoulder-charged.

`Assembly-CSharp.csproj` compiles: **0 errors**.

---

## What this invalidates

Every shipped `.onnx` takes a 75-float observation and was trained on a 49.8 kg body whose legs could
pass through each other. All four are now stale:
`athlete_run.onnx`, `athlete_track.onnx`, `athlete_getup.onnx`. They are left in place, untouched, as
the comparison baseline. (`athlete_isaac.onnx` was the fourth until it was deleted with the rest of
the Isaac Lab twin on 2026-09-14.)

`training/.venv` was missing entirely and has been rebuilt. There is still no `checkpoints/`, so the
first run cannot `--resume`.

## Running it

```
cd training
.venv/Scripts/python.exe train_run.py --task target --iters 2000
```

TensorBoard starts automatically on http://localhost:6006 and stale runs for the task are cleared first
(house rules 9 and 10). Watch `env/duty_factor`, `env/air_time` and `env/foot_slip` alongside the return:
they are what say whether the thing is running or shuffling.

At 8192 envs and ~328k steps/s, 2000 iterations of 24 steps is roughly **20 minutes**. House rule 12
applies past 30 minutes — close the Unity Editor first for the full three-task sequence.

Suggested order: `target` first (it is the base gait), then `track` resumed from it, then `getup`.
`--speed-adaptive` is worth trying now that the ankle can actually produce sprint torque; the old 4.5 m/s
ceiling was partly a 100 N m ankle, not only the reward.
