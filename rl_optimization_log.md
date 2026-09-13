# MuJoCo RL optimization log

Autonomous two-phase optimization pass on the PoDecath athlete locomotion policy.
Started 2026-09-13 15:56 EDT. Host: RTX 5070 Ti Laptop (12 GB, sm_120), Core Ultra 9 275HX, 32 GB RAM.
Stack: torch 2.11.0+cu128, warp 1.17.0, mujoco 3.13.0, mujoco_warp @ main.

Task under test: `target` (run-to-target), `training/envs/run_to_target.py` on
`training/models/athlete.xml` — 75 kg humanoid, 21 actuated DoF, 200 Hz physics, 50 Hz control
(decimation 4), 20 s episodes, target speed 3.5 m/s.

---

## PHASE 1 — metric discovery and baseline

### 1.1 What the audit found already in place

`training/STAGED_TUNING.md` (written earlier the same day) had already landed a deep physics and
reward pass that **had never been trained on** — the only prior run was a three-iteration smoke test.
Re-auditing against this goal's Phase 2 checklist, most of the MJCF items were already satisfied, so
they are recorded here as *verified, no action* rather than re-done:

| Phase 2 checklist item | State found | Action |
|---|---|---|
| Anatomical `<joint>` limits / damping | Per-joint ranges from human data, `damping` 0.5, `armature` 0.02–0.10 per group | verified, none |
| `<contact><exclude>` parent–child pairs | 11 exclude pairs present; geoms `contype=1 conaffinity=1`; `margin=0` (required by MULTICCD) | verified, none |
| Solver type / iterations / timestep | `implicitfast`, `iterations=10`, `ls_iterations=10`, `timestep=0.005`, `eulerdamp` disabled | verified, none |
| Contact budget | `nconmax=48`, `njmax=192` **per world** | verified, none |
| Vectorised envs / headless | 8192 GPU worlds via `mujoco_warp`, CUDA-graph capture of the decimation loop, TF32 on | verified, none |
| Per-step Python allocations | Step path is sync-free; masked resets via `torch.where`, stats accumulate as GPU scalars | verified, none |
| Actuator-force + joint-speed reward penalties | `r_act`, `r_rate`, `r_energy` (cost of transport), `r_qvel` (human peak speeds) all present and clamped | verified, none |
| Observation normalisation | Running mean/std baked into the exported ONNX graph | verified, none |

So Phase 1's real work was **not** re-tuning physics. It was that the run had no way to *grade*
itself: the reward had terms for effort and stability, but nothing reported effort or stability as a
number you could compare against a threshold.

### 1.2 Metrics that were missing, and were added

Reward terms say what the policy was *asked* for. Metrics say what it *did*. Only the second can be
checked against a convergence threshold. Eight were missing and are now instrumented in
`RunToTargetEnv._accumulate_kpis` (`training/envs/run_to_target.py`), inherited by the `track` task,
and written to TensorBoard, the CSV (`training/logs/run_to_target.csv`) and a new console line:

| Metric | Meaning | Why it was needed |
|---|---|---|
| `surv_ratio` | completed episode length / 20 s nominal | `fall_rate` alone conflates "fell" with "no episode ended this iteration" |
| `v_err` | mean \|v_toward − target_speed\| (m/s) | `v_toward` was logged raw; the *error* against the command was not |
| `v_hit_frac` | fraction of steps within ±10 % of the command | the actual pass/fail form of the velocity KPI |
| `torque` | mean per-joint \|actuator force\| (N·m) | control cost existed as a reward term, never as a reading |
| `power` | whole-body \|τ·ω\| (W) | mechanical cost of transport as a measured figure |
| `jerk` | RMS joint jerk (rad/s³), from the qvel→qacc→jerk chain | the action-rate penalty is a proxy for chatter; this is the thing itself |
| `pitch_dev`, `roll_dev` | torso attitude off vertical (degrees) | `upright` is a cosine — it cannot separate pitch from roll, and 0.98 "sounds fine" while being 11° |
| `act_sat` | fraction of action components sitting on the ±3 clip | a policy pinned to its clip is doing bang-bang control in a zero-gradient region |

All are sync-free GPU accumulators read once per iteration, so instrumentation costs no measurable
throughput. Jerk history (`prev_qvel_j`, `prev_qacc_j`) is cleared on episode reset — carrying it
across a reset would book one enormous fake spike per episode and swamp the average.

**One bug found and fixed in the instrumentation itself during the smoke test.** `pitch_dev` and
`roll_dev` both read exactly 90° while `upright` read 0.976. Cause: `-grav_b[:, 2].clamp_min(1e-6)`
parses as `-(grav_b[:,2].clamp_min(1e-6))`, and since the standing value is negative the clamp
returned `1e-6` and the negation made it `-1e-6` — `atan2` of anything against ~0 is ±90°.
Corrected to `(-grav_b[:, 2]).clamp_min(1e-6)`. Post-fix the tilt agrees with `upright`
(0.976 ↔ 7.6°/1.6°). Recorded because the same precedence trap is easy to repeat.

### 1.3 Throughput baseline — the documented figure was not the training figure

`STAGED_TUNING.md` records **283k–328k steps/s** at 8192 environments. Measured end to end in the
actual training loop: **~177,000 steps/s** (mean over iterations 2–11, min 167k, max 190k).

Both numbers are correct and they measure different things. The documented benchmark timed *physics
only* — 120 `env.step` calls after warm-up, no learning. The training loop additionally runs the PPO
update every iteration: 5 epochs × 8 minibatches = **40 gradient steps** over 24,576 samples each.
The gap implies the update is consuming roughly **45 % of wall-clock time**, which makes it the
single largest remaining throughput lever — larger than anything left in the physics.

This is the main Phase 1 discovery and it sets the Phase 2 agenda.

### 1.4 Baseline run

`train_run.py --task target --run-name baseline_kpi`, all defaults, 8192 environments, domain
randomisation on, seed 0. Stopped at **226 iterations** (44.4 M environment steps) once the trend was
monotonic and unambiguous; the CSV is `training/logs/baseline_kpi.csv`.

| iter | ep_return | surv_ratio | fall_rate | ep_len_s | v_toward | v_err | air_time | duty | slip | torque | power | jerk | pitch | roll | std |
|-----:|----------:|-----------:|----------:|---------:|---------:|------:|---------:|-----:|-----:|-------:|------:|-----:|------:|-----:|----:|
| 10 | -71.8 | 0.059 | 1.000 | 1.18 | 0.06 | 3.45 | 0.070 | 0.73 | 0.81 | 27.7 | 1370 | 16238 | 17.3 | 8.9 | 0.74 |
| 50 | -20.1 | 0.056 | 1.000 | 1.13 | 0.46 | 3.04 | 0.274 | 0.44 | 0.43 | 20.9 | 736 | 11000 | 10.7 | 10.7 | 0.54 |
| 100 | +0.5 | 0.057 | 1.000 | 1.14 | 0.65 | 2.85 | 0.559 | 0.35 | 0.35 | 18.2 | 526 | 8418 | 10.5 | 12.9 | 0.42 |
| 150 | +24.6 | 0.071 | 1.000 | 1.43 | 0.85 | 2.66 | 0.571 | 0.28 | 0.28 | 17.6 | 505 | 7282 | 10.7 | 12.5 | 0.35 |
| 200 | +45.0 | 0.079 | 1.000 | 1.59 | 1.05 | 2.46 | 0.585 | 0.26 | 0.26 | 17.6 | 525 | 6758 | 10.0 | 14.6 | 0.32 |
| 215 | +48.9 | 0.080 | 1.000 | 1.60 | 1.08 | 2.42 | 0.614 | 0.26 | 0.26 | 17.6 | 530 | 6634 | 10.0 | 15.1 | 0.31 |

### 1.5 The baseline is reward hacking, and only the new metrics show it

Read the return column alone and this is a healthy run: **-71.8 to +48.9, a 121-point improvement over
215 iterations, still climbing.** Every other reading says it is not.

- `fall_rate` **never leaves 1.000**. Every episode, for 215 iterations, ends in a fall.
- `surv_ratio` moves 0.059 -> 0.080. The athlete lasts **1.6 s of a 20 s episode**.
- `air_time` climbs monotonically **0.07 -> 0.61 s**, and `duty_factor` falls **0.73 -> 0.26**.
- `roll_dev` gets **worse**, 8.9 deg -> 15.1 deg, while the return improves.

0.61 s of flight per footfall is not a stride. A human sprint stride flies 0.10-0.20 s. The body is
airborne for most of a 1.6 s episode because it is falling, and `duty_factor` 0.26 -- which in a
table of gait numbers reads like a fast run -- is the same fact stated differently.

**The mechanism.** `r_air` pays `(air_time - 0.25)` per footfall with **no upper bound**:

```python
r_air = 1.0 * ((self.prev_air - 0.25) * first_contact.float()).sum(-1) * moving
```

The cheapest way to be off the ground for a long time is not to run. It is to fall over. The policy
found that, and the rising return is very largely this one term. Isaac Lab's equivalent
`feet_air_time` clamps for exactly this reason; this copy dropped the clamp.

This is worth stating plainly because it is the trap the whole exercise exists to catch: **the run
looked like it was working.** `STAGED_TUNING.md` added the gait terms specifically so that
`v_toward` could not pass a shuffle off as a run, and it succeeded -- but the new terms then became
the thing to game, and nothing reported survival as a number until Phase 1 added `surv_ratio`.

### 1.6 Throughput and hardware efficiency

| measurement | value |
|---|---|
| End-to-end training, 8192 envs, uncontended | **177,000 steps/s** (mean iters 2-11) |
| Same, once most bodies are fallen and tumbling | ~120,000 steps/s (contact count rises) |
| Documented physics-only figure (`STAGED_TUNING.md`) | 283k-328k steps/s |
| GPU utilisation during training | 76-83 %, **38 W**, 3.7 GB of 12 GB |
| Two concurrent 8192-env runs | 120k + 60k = ~180k aggregate -- **no gain** |

The concurrency test settles the "is there headroom" question: 83 % utilisation and 38 W look like
spare capacity, but a second training process bought **zero** aggregate throughput. The GPU is
already saturated at 8192 environments; the low power figure reflects a kernel-launch-bound
workload, not an idle one. **Running experiments sequentially is therefore the correct use of this
hardware**, and the remaining throughput lever is the PPO update, not more parallelism.

### 1.7 Convergence thresholds (the exit criteria, quantified)

Fixed here so later runs are graded, not eyeballed. Measured over the last 25 iterations of a run.

| KPI | Threshold | Baseline @215 | Met? |
|---|---|---|---|
| `surv_ratio` (survival step ratio) | **> 0.90** | 0.080 | no |
| `fall_rate` | **< 0.10** | 1.000 | no |
| `v_err` (\|v - 3.5\| m/s) | **<= 0.35** (+-10 %) | 2.42 | no |
| `v_hit_frac` | **> 0.50** | 0.000 | no |
| `air_time` (plausible stride) | **0.05-0.25 s** | 0.614 | no |
| `duty_factor` | **0.35-0.65** | 0.259 | no (only by way of falling) |
| `foot_slip` | **< 0.15 m/s** | 0.259 | no |
| `pitch_dev` / `roll_dev` | **< 15 deg / < 10 deg** | 10.0 / 15.1 | no |
| `torque` (control effort) | **<= baseline (17.6 N m)** | 17.6 | ref |
| `jerk` | **<= baseline (6634)** | 6634 | ref |

Plateau rule for stopping early: **< 3 % change in `surv_ratio` and `ep_return` over 3 consecutive
runs.**

---

## PHASE 2 — optimizing against the discovered KPIs

Each experiment is 300 iterations from seed 0, identical in every respect except the named change,
and graded by the mean of its last 25 iterations against the baseline at the same iteration count.
Iteration 0 reproduces bit-for-bit across runs (return -12.6755 in both baseline and E1), so the
comparisons are controlled.

### E1 - cap the air-time credit (reward shaping)

`--air-time-cap 0.4`: `r_air` pays on `min(air_time, 0.4) - 0.25` instead of an unbounded
`air_time - 0.25`. 0.4 s is already double a human sprint's flight phase, so it removes the exploit
without touching a legitimate stride. `0` restores the old behaviour and keeps the baseline
reproducible.

**Result: the cap works as designed and does not fix the problem.** At iteration 100, `air_time`
0.42 against the baseline's 0.56, and the return is slightly lower (-1.3 vs +0.5) exactly as a
removed reward should make it. But `fall_rate` is still **1.000** and `ep_len_s` still **1.1 s**.

| iter 100 | baseline | E1 (cap 0.4) |
|---|---|---|
| ep_return | +0.5 | -1.3 |
| air_time | 0.559 | 0.42 |
| duty_factor | 0.354 | 0.36 |
| fall_rate | 1.000 | 1.000 |
| ep_len_s | 1.14 | 1.1 |

So capping the term changed the term and changed nothing else. **The Section 1.5 diagnosis was
wrong**, and E1 is what proved it. Kept anyway: the cap is still correct (0.61 s of paid flight is
not a stride any human runs), it is just not the disease. Run stopped at 118 iterations.

### E1a - the diagnostic that should have come first: per-term reward accounting

The Section 1.5 mistake was avoidable. The return was being attributed to a term by eye, when
nothing logged what each term actually contributed. That is now instrumented: all 21 reward terms
accumulate as GPU scalars and are reported per step, to TensorBoard and the CSV, so they sum to the
mean step reward and can be read against each other.

Measured on the **baseline's own iteration-200 policy** (resumed from `model_00200.pt`, uncapped
reward, averaged over 10 iterations):

```
mean step reward +0.6079   (positive +1.3452, negative -0.7372)
     alive   +0.3000   +22.3%        slip    -0.3243   -44.0%   <-- largest penalty by 3x
   upright   +0.2761   +20.5%        energy  -0.1054   -14.3%
      prog   +0.2715   +20.2%        rate    -0.0835   -11.3%
     track   +0.2397   +17.8%        limit   -0.0542    -7.3%
   heading   +0.1349   +10.0%        ang     -0.0530    -7.2%
       arm   +0.1167    +8.7%        act     -0.0315    -4.3%
       air   +0.0063    +0.5%  <--   fall    -0.0250    -3.4%   <-- falling is nearly free
```

`r_air` is **0.5 % of the positive reward**. It was never the driver. Two numbers matter instead:

1. **`r_slip` is -0.324 per step, 44 % of every penalty the policy pays, 3x the next largest.**
2. **`r_fall` is -0.025 per step.** The -2.0 termination penalty spread over an 85-step episode is
   almost nothing. Falling is punished **13x more weakly** than sliding a foot.

Given that pair, keeping the feet off the ground is the rational policy, and `duty_factor` 0.26 and
`air_time` 0.61 are the *consequence* of avoiding the slip penalty, not an attempt to farm `r_air`.

### E1b - why the slip penalty was so large: a reset bug

`r_slip` should be near zero for a foot that is planted. Measured on a normally-stepping
environment it is: **-0.000**. On the first step after an episode reset it is **-24.5**.

`_apply_reset` zeroed `prev_foot_xy`:

```python
self.prev_foot_xy = torch.where(m2[:, :, None], torch.zeros_like(self.prev_foot_xy), self.prev_foot_xy)
```

`prev_foot_xy` holds a **world position**, not a delta. Zeroing it makes the next step compute
`foot_v = (foot_position - 0) / dt`. For an athlete standing a few metres from the origin that is
**6.8 m/s measured**, which squares to well past the term's own 25.0 clamp, so the episode opens by
handing the policy the **maximum possible slip penalty for a body that has not moved.**

The full `reset()` never had this bug — it seeds `prev_foot_xy` from the real foot position
(line 287). Only the masked per-episode reset, which is the one that runs millions of times, zeroed it.

**The arithmetic closes exactly.** -24.5 once per episode over the 85-step episodes this run was
producing is **-0.288 per step**. The measured `r_slip` was **-0.324 per step**. The single largest
penalty in the reward was almost entirely an artefact of the reset.

**Fix.** `prev_foot_xy` is no longer zeroed; it is re-seeded from the real foot position in `step`,
after `_physics_forward` has placed the new pose:

```python
self.prev_foot_xy = torch.where(done[:, None, None],
                                self.site_xpos[:, self.foot_sites, :2], self.prev_foot_xy)
```

Verified: the first post-reset step now scores **+0.455** (against +0.542 for an untouched
environment) instead of roughly -24.

This also means the reported **`foot_slip` KPI was inflated by the same artefact** — the baseline's
0.26 m/s is mostly one reset spike per episode, not sliding.

### E2 - the reset fix, isolated

`--air-time-cap 0` so the only difference from the baseline is the `prev_foot_xy` fix. 250
iterations, seed 0. Compared at **iteration 225**, mean of the last 25 iterations.

| KPI | want | baseline | E2 (reset fix) | delta |
|---|---|---:|---:|---:|
| `ep_return` | up | 49.18 | **76.86** | **+56.3 %** |
| `foot_slip` | down | 0.260 | **0.106** | **-59.5 %** |
| `roll_dev` | down | 15.08 | **9.07** | **-39.8 %** |
| `pitch_dev` | down | 9.96 | **7.39** | **-25.7 %** |
| `act_sat` | down | 0.005 | 0.001 | -87.3 % |
| `air_time` | 0.05-0.25 | 0.598 | 0.451 | -24.7 % |
| `duty_factor` | 0.35-0.65 | 0.261 | 0.296 | +13.3 % |
| `v_toward` | up | 1.084 | 1.121 | +3.4 % |
| `torque` | down | 17.63 | 18.35 | +4.1 % |
| `power` | down | 531 | 555 | +4.4 % |
| `surv_ratio` | up | 0.080 | 0.080 | +0.1 % |
| `fall_rate` | down | 1.000 | 1.000 | 0.0 % |

**Learning speed.** E2 reaches a return of +0.5 by iteration 40 and +24 by iteration 90; the baseline
needed iteration 100 and iteration 150 for the same marks. Roughly **2x faster to the same return**,
from removing one artefact.

**Every stability KPI improved**, and the two that got worse are the ones that should: `torque` and
`power` are up 4 % because the athlete is now doing more real work (moving 3 % faster) instead of
holding its feet in the air to dodge a phantom penalty.

**`surv_ratio` and `fall_rate` did not move at all.** This is the honest result: the reset bug was
real, worth fixing, and is not by itself what stops this humanoid standing up.

### E3 - make falling cost something

From the term accounting, falling is worth -0.025 per step and the slip term was -0.324. Even with
the artefact gone, terminating is close to free in the immediate reward; the real cost of a fall (the
alive + upright + progress income forfeited for the rest of a 1000-step episode) has to reach the
policy through the value function across a **24-step GAE window**, which at gamma 0.99 sees about
0.48 s ahead of a 20 s episode.

`--fall-penalty 20.0` makes termination worth about -0.24 per step over the ~85-step episodes this
run produces -- comparable to the largest positive term, rather than an order of magnitude below it.
Run with the reset fix and `--air-time-cap 0.4` (both now defaults), 200 iterations.

**Result: the first movement in survival this whole exercise has produced.** All three runs compared
at **iteration 199**, mean of the last 25:

| KPI | want | baseline | E2 (reset fix) | E3 (+ fall 20) | E3 vs baseline |
|---|---|---:|---:|---:|---:|
| `surv_ratio` | up | 0.078 | 0.078 | **0.094** | **+21.3 %** |
| `ep_len_s` | up | 1.556 | 1.551 | **1.887** | **+21.3 %** |
| `roll_dev` | down | 14.04 | 9.06 | **7.19** | **-48.8 %** |
| `foot_slip` | down | 0.269 | 0.108 | **0.116** | **-57.0 %** |
| `duty_factor` | 0.35-0.65 | 0.265 | 0.302 | **0.337** | **+27.2 %** |
| `air_time` | 0.05-0.25 | 0.571 | 0.452 | **0.399** | -30.1 % |
| `pitch_dev` | down | 10.27 | 7.97 | 9.23 | -10.1 % |
| `act_sat` | down | 0.003 | 0.000 | 0.001 | -84.2 % |
| `ep_return` | up | 40.45 | **66.47** | 48.72 | +20.5 % |
| `v_toward` | up | 1.002 | 1.010 | 0.842 | **-16.0 %** |
| `torque` | down | 17.57 | 17.88 | 19.40 | **+10.4 %** |
| `jerk` | down | 6867 | 6807 | 7322 | **+6.6 %** |
| `fall_rate` | down | 1.000 | 1.000 | 1.000 | 0.0 % |

Per-term accounting on E3 confirms the knob did what it was set to do -- `fall` is now the largest
penalty at **-0.203 per step** (it was -0.025), ahead of `energy` -0.107 and `rate` -0.099.

The trade is visible and expected: the athlete is **more cautious**. It stays up 21 % longer and its
torso rolls half as far, and it pays for that with 16 % less speed and 10 % more torque. For a policy
that currently falls in under two seconds, that is the right side of the trade.

`duty_factor` 0.337 is the first reading that lands at the edge of the plausible human band
(0.35-0.65) rather than far below it, and `air_time` has come down 0.571 -> 0.399. Both are moving
toward a gait rather than a topple.

### E4 - turn the exploration noise down

The hypothesis the other three runs kept pointing at: the action standard deviation was still
**0.31-0.35** at the end of every run. With `action_scale` 0.5 that is about +-0.16 rad of noise
injected into all 21 joints at 50 Hz, on a body whose entire task is not to topple. And the body
*can* stand -- driven with zero actions it holds the stand pose (`fall_rate` 0.04 over 60 steps). So
some of the falling may simply be the exploration noise knocking it over.

`--entropy-coef 0.001` (from 0.005), on top of E3's `--fall-penalty 20.0`. 200 iterations.

**Result: the best configuration tested, and it dominates E3 almost everywhere.** Compared at
iteration 199:

| KPI | want | baseline | E3 (fall 20) | E4 (+ entropy 0.001) | E4 vs baseline |
|---|---|---:|---:|---:|---:|
| `surv_ratio` | up | 0.078 | 0.094 | **0.101** | **+30.4 %** |
| `ep_len_s` | up | 1.556 | 1.887 | **2.029 s** | **+30.4 %** |
| `ep_return` | up | 40.45 | 48.72 | **63.45** | **+56.9 %** |
| `roll_dev` | down | 14.04 | 7.19 | **6.86** | **-51.1 %** |
| `foot_slip` | down | 0.269 | 0.116 | 0.125 | **-53.5 %** |
| `air_time` | 0.05-0.25 | 0.571 | 0.399 | **0.394** | -31.0 % |
| `duty_factor` | 0.35-0.65 | 0.265 | 0.337 | 0.330 | +24.5 % |
| `pitch_dev` | down | 10.27 | 9.23 | **9.17** | -10.8 % |
| `jerk` | down | 6867 | 7322 | **6658** | **-3.0 %** |
| `power` | down | 517 | 528 | **509** | -1.6 % |
| `torque` | down | 17.57 | 19.40 | 18.97 | +8.0 % |
| `v_toward` | up | 1.002 | 0.842 | **0.940** | -6.2 % |
| `fall_rate` | down | 1.000 | 1.000 | 1.000 | 0.0 % |

Lowering the entropy bonus **recovered most of the speed E3 gave up** (0.84 -> 0.94 m/s) while still
improving survival, and it **undid E3's jerk regression** (+6.6 % -> -3.0 %) and its power cost
(+2.0 % -> -1.6 %). Cautious and smooth rather than cautious and stiff.

Episodes now last **2.03 s against the baseline's 1.56 s**, and the torso rolls **half as far**.
`fall_rate` is still 1.000.

---

## Results summary

### What was wrong, in order of how much it mattered

1. **`prev_foot_xy` was zeroed on episode reset** (`envs/run_to_target.py`). A world position treated
   as if it were a delta, so the first step of every episode measured a 6.8 m/s foot velocity and
   booked the maximum slip penalty, -24.5, for a body that had not moved. This was **44 % of every
   penalty in the reward**, and the policy's rational response was to keep its feet off the ground.
   Fixing it: **+56 % return, -60 % foot slip, -40 % roll, and roughly 2x faster learning.**
2. **Falling was 13x cheaper than sliding a foot.** -2.0 once per episode is -0.025 per step. Raising
   it to -20.0: **+21 % survival, +21 % episode length, -49 % roll.**
2b. **The exploration bonus was too high for a balance task.** `--entropy-coef` 0.005 -> 0.001 on top
   of (2): survival **+30 %** over baseline, episodes **2.03 s vs 1.56 s**, and it recovered the
   speed and smoothness the fall penalty alone had cost.
3. **Nothing measured survival, effort, or attitude.** Eight KPIs and 21 per-term reward readouts
   added. Without the term accounting the first diagnosis (Section 1.5) was wrong and stayed wrong.
4. **The documented throughput figure was physics-only.** 177k steps/s end to end, not 283k-328k.
5. **A sign-precedence bug in the new tilt metric**, found and fixed during its own smoke test.

### Exit criteria -- which one actually fired

The **time limit**. Not convergence, and not plateau.

| Exit condition | Status |
|---|---|
| All KPIs met over 10 consecutive evaluation episodes | **not met** -- best `surv_ratio` 0.101 against a 0.90 threshold |
| Improvement plateau < 3 % over 3 runs | **not met** -- every run was still improving when it stopped |
| Time limit (1 hour) | **fired** |

This needs stating plainly: **no configuration tested here produces a humanoid that stays on its
feet.** `fall_rate` is 1.000 in all five runs. What the hour bought is a correctly instrumented
trainer, two real bugs out of the reward, and a measured direction of travel -- not a trained policy.

### The runs are far too short to conclude anything about convergence

Every run here is 200-250 iterations. `STAGED_TUNING.md` budgets **2000 iterations** for this task and
the shipped lap policy took **2300**. These runs are about **10 % of one**, which is enough to compare
two configurations against each other and **not** enough to say whether either converges. The
baseline, E2 and E3 were all still improving monotonically at the iteration they were stopped.

### Recommended next run

```
cd training
.venv/Scripts/python.exe train_run.py --task target --iters 2500 \
    --fall-penalty 20.0 --entropy-coef 0.001 \
    --speed-adaptive --target-speed-final 6.0
```

The reset fix, the air-time cap (0.4) and per-term logging are already the defaults.
`--fall-penalty` still defaults to the original 2.0 and `--entropy-coef` to 0.005; both should be
overridden as above. That pair (E4) is the best configuration measured here. At ~130k steps/s that run is roughly **80 minutes**, so house rule 12 applies --
close the Unity Editor first.

Watch `env/surv_ratio` first and `env/ep_return` second. A run whose return climbs while
`surv_ratio` sits flat is the exact failure this exercise found, and it is now visible on the
TensorBoard page instead of having to be inferred.

### Where to push next

The exploration-noise hypothesis was tested (E4) and paid: `--entropy-coef 0.001` is now part of the
recommended configuration. It has **not** been pushed to its limit -- the action std still ends
around 0.31. Lowering `init_std` (0.8) directly, or dropping `--entropy-coef` further, is the
obvious next A/B and this harness runs one in about five minutes.

### Left alone deliberately

- **PPO update shape** (epochs x minibatches). The 45 % of wall clock it costs is the largest
  throughput lever, but cutting gradient steps trades sample efficiency for speed, and with the
  reward this unsettled that trade could not be measured honestly in the time available.
- **The adaptive learning rate thrash.** The controller adjusts once per minibatch, 40 times an
  iteration at +-50 % each, and the rate swung between 1e-5 and 3.4e-3 from iteration to iteration.
  A `--lr-adapt` knob is wired up (try 1.1) but was never A/B'd.
- **MJCF physics.** Audited, already correct, not touched. See Section 1.1.

### Throughput note

The `fps` column in the comparison table is **not** a valid A/B: the baseline shared the GPU with E1
for part of its run. The clean figures are Section 1.6's -- 177k steps/s uncontended, falling toward
120k as bodies tumble and contact counts rise.

### Files

| Path | What |
|---|---|
| `training/envs/run_to_target.py` | KPI accumulators, per-term reward logging, `prev_foot_xy` fix, `air_time_cap`, `fall_penalty` |
| `training/train_run.py` | KPI + term CSV columns, KPI console line, `--air-time-cap` / `--fall-penalty` / `--lr-adapt` / `--epochs`, per-run CSV |
| `training/ppo.py` | `lr_adapt` config field |
| `training/logs/baseline_kpi.csv`, `e1_aircap.csv`, `e2_slipfix.csv`, `e3_fallpen.csv`, `e4_lownoise.csv`, `diag_terms.csv` | per-run KPI history |

`Assets/Policies/athlete_run.onnx` was overwritten by a diagnostic run's end-of-run auto-export and
has been **restored from git**. All four shipped `.onnx` files are untouched, as `STAGED_TUNING.md`
intends. Experiment runs since then export to `training/logs/scratch_policies/`.

---

## TL;DR

Fixed a reset bug that made 44 % of the reward a phantom penalty, raised the fall penalty and cut
exploration noise: +57 % return, +30 % survival, half the torso roll. Still falls every episode.
