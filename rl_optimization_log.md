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
found that, and the rising return is very largely this one term. The reference implementation of
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

---

# SESSION 2 — 2026-09-13 23:40 to 2026-09-14

Resumed against the same goal. The log above ends at 16:33; between then and 23:19 three more runs
happened (`overnight_3h`, `contactfix_3h`, and a three-way A/B `f0/f1/f2`) and three commits landed,
none of them written up here. This section starts by grading those, and then follows where they lead.

Tooling added first, because every number below depends on it:

| Tool | Question it answers |
|---|---|
| `training/tools/grade.py` | what did a run actually score against the Section 1.7 thresholds, side by side with any other run |
| `training/tools/tb_read.py` | what was a finished run's target speed / any scalar, recovered from its TensorBoard events |
| `training/tools/stand_check.py` | can the body hold its own stand pose, and where is its weight over its feet |
| `training/tools/stand_trace.py` | which joint gives way when it cannot |
| `training/tools/gain_sweep.py` | how much joint stiffness would it take |
| `training/tools/pose_sweep.py` | can a better stand pose replace that stiffness |
| `training/tools/stand_robust.py` | does a candidate survive the noise the trainer actually resets with |
| `training/tools/noise_robust.py` | does it survive the noise the *policy* injects |

## 2.1 Grading the runs the previous session did not see

### The three-way A/B on the standing-still failure (f0/f1/f2)

220 iterations each, 4096 environments, run concurrently so they are mutually controlled.

| KPI | f0 control | f1 track_var 8 | f2 track_var 8 + fall 5 |
|---|---:|---:|---:|
| `ep_return` | 40.1 | 103 | 108 |
| `surv_ratio` | 0.109 | 0.134 | 0.113 |
| `fall_rate` | 1.000 | 0.9997 | 1.000 |
| `v_toward` | 0.495 | 0.440 | 0.810 |
| `rt_track` | 0.060 | 0.483 | 0.623 |

Widening the tracking kernel did what it was designed to do to the *term* (`rt_track` 0.06 -> 0.48)
and **nothing to the speed** (`v_toward` 0.495 -> 0.440, slightly worse). The speed gain in f2 comes
from the other knob in it, the fall penalty dropping 20 -> 5. Since there is no run with the widened
kernel at the old fall penalty held separately, f1 is the one that isolates it, and it says the
kernel was not the problem. Worth stating because the commit message that launched these runs
attributes the standing-still failure to the kernel.

### The two three-hour runs

| KPI | `overnight_3h` (7105 iters) | `contactfix_3h` (1154 iters) |
|---|---:|---:|
| `surv_ratio` | 0.703 | 0.654 (peak 0.929 at iter 420) |
| `v_toward` | 2.01 | 0.028 |
| `duty_factor` | 0.023 | 0.973 |

`overnight_3h` is the best result this project has produced — 2 m/s with 70 % survival over 1.4
billion environment steps — and **it is not reproducible or usable**, for two separate reasons.

First, its gait numbers are measured with the pre-fix contact test, so `duty_factor` 0.023 and
`air_time` 3.2 s are the artefact commit 46062a4 describes, not readings. Second, and worse: the
contact fix changed two of the policy's 78 *observations*, so `overnight_3h`'s weights are now being
fed a vector they never trained against. Evaluated in plain MuJoCo through `eval_100m.py` the policy
**falls after 1.5 s and covers 1.5 m**, against the 2 m/s the training curve claims.

That the harness is not at fault was checked against `contactfix_3h`'s own checkpoint, which trained
*with* the corrected contact test: it stands for **47 s and travels -0.2 m**, exactly the statue its
training curve describes. The harness is right; the older policy is out of distribution.

**The contact fix invalidated every policy trained before it, and that was not recorded anywhere.**

### Two things found while grading, both fixed

- **Runs did not record their own arguments.** Two three-hour runs left a CSV, a log and a TensorBoard
  directory between them and not one command-line argument, so their target speed had to be recovered
  by reading `env/target_speed` back out of the event files and everything else was unrecoverable.
  `train_run.py` now writes `logs/<run>.args.json` and echoes the command line.
- **Checkpoints were not namespaced per run.** Every run wrote `model_<iter>.pt` into one directory
  shared by the whole task, so short A/Bs overwrote long runs' checkpoints and three concurrent A/Bs
  interleaved their saves into a sequence belonging to none of them. `overnight_3h`'s weights
  survived only because no short run ever reached iteration 7106. They are preserved in
  `training/checkpoints/keep/` and new runs write to `checkpoints/<task>/<run>/`.

## 2.2 The speed curriculum was built to ramp the wrong way

`--speed-adaptive` moves the target between `--target-speed` (the floor) and `--target-speed-final`
(the ceiling). Every run in this project set `--target-speed 3.5`, so the curriculum's *floor* was a
sprint and it could only ever ramp upward from one. There has never been a walk-first curriculum;
the flag to build one already existed and was being handed the wrong argument.

## 2.3 Batch G — the double-support penalty is not the blocker, and low speed buys survival by standing still

Three runs against `f2_trackvar_fall`, 220 iterations, 4096 environments, one knob each.

| KPI | f2 (3.5 m/s) | g1 walk (1.5 m/s) | g2 no alt penalty | g3 both |
|---|---:|---:|---:|---:|
| `surv_ratio` | 0.113 | **0.817** | 0.099 | **0.848** |
| `fall_rate` | 1.000 | **0.256** | 1.000 | **0.204** |
| `ep_len_s` | 2.26 | **16.4** | 1.99 | **17.0** |
| `v_toward` | 0.810 | 0.089 | 0.845 | 0.087 |
| `duty_factor` | 0.611 | 0.966 | 0.664 | 0.978 |

`r_alt`, the penalty for having both feet down while moving, was the suspect: a run has no
double-support phase but every walk does, and at 0.5 per step it costs two thirds of the whole
posture income. **Removing it changes nothing** (g2 vs f2: `surv_ratio` 0.099 vs 0.113). Hypothesis
rejected cleanly; `--alt-w` stays as a knob, defaulted to its original 0.5.

Asking for a walk instead of a run moves survival from 0.11 to 0.82 — and `v_toward` collapses to
0.09 with `duty_factor` 0.97. It is not walking. It is standing still and collecting the posture
income, the same statue `contactfix_3h` converged on, reached in 220 iterations instead of 400.

So the athlete has exactly two behaviours available to it: **fall over in two seconds, or stand
still.** Nothing in between. That is not a reward-weighting problem, and this is where the reward
tuning stopped being the right place to look.

## 2.4 The body cannot stand up

`tools/stand_check.py` holds the model's own stand keyframe with **zero action** — no policy, no
noise, the actuators simply asked to hold the pose the rig was built in.

```
t= 0.0s  pelvis_z 0.902  upright 1.000  CoM at  16.7% heel->toe
t= 1.0s  pelvis_z 0.897  upright 0.981  CoM at -16.4% heel->toe
t= 2.0s  pelvis_z 0.135  upright 0.049  CoM at -1177%
```

**It topples backwards and is on the floor in 1.75 s.** That number is the episode length every run
in this project has ever reported: baseline 1.56 s, E2 1.55 s, E3 1.89 s, E4 2.03 s, f0 2.18 s,
f2 2.26 s. Every one of them is the passive topple time of a body that was falling over before the
policy did anything at all.

Under the noise the trainer actually resets with (joints +-0.05 rad, base +-0.2 m/s, kp x[0.8, 1.25]),
**0 of 40 trials stayed upright for 5 s.** A perfect policy that output nothing but zeros could not
have passed the survival threshold on this model.

Two causes, both measured:

1. **The stand pose is out of balance.** The centre of mass sits at **17 % of the heel-to-toe span**
   where a standing human sits near 45 %, and the foot has 6.4 cm of heel behind the ankle against
   27 cm of toe in front. The torso capsule leans back (`fromto` x runs 0.02 -> -0.058) and the head
   sits at x = -0.027, so 44 % of the body mass is behind the pelvis.
2. **The joints are too soft to hold an inverted pendulum.** `stand_trace.py` shows `abdomen_y`
   drifting -0.086 -> -0.42 rad and the knees and hips giving way with them — while every actuator
   involved sits at **10-38 % of its force limit**. Nothing saturates; the gains are simply below the
   stability floor. For a body of mass M with its centre of mass h above the ankle that floor is
   M*g*h = 75 * 9.81 * 0.9 = **662 N m/rad**, against the **250** the ankle shipped with.

Raising the ankle alone does not fix it (the lean is spread across hip, knee and ankle, and the ankle
never exceeds 20 N m). A sweep of both together:

| | shipped | +3 deg lean | 3x stiffness | both |
|---|---:|---:|---:|---:|
| held 10 s, zero action | 1.75 s | 3.14 s | 2.45 s | **10 s** |
| survived 5 s, noisy resets | **0 %** | 0 % | 2 % | **90 %** |

**The fix, applied in the generator rather than by hand** (`rig_to_mjcf.py` regenerates
`models/athlete.xml` byte-for-byte, verified before changing anything):

- `STAND_DEG`: `ankle_y` -3 deg, `hip_y` +3 deg — a small forward lean with the trunk kept vertical,
  moving the centre of mass from 17 % to 37 % of the foot.
- `rigs/matt.json` gains: abdomen, hip, knee and ankle `kp` and `kv` x3 (150/200/200/250 ->
  450/600/600/750 N m/rad). **Force limits and velocity limits are unchanged**, so peak joint torque
  stays at the human figures house rule 15 asks for — 240 N m at the hip, 260 at the knee, 220 at the
  ankle. What changes is how hard the joint corrects *within* those limits, which is the neuromuscular
  loop rather than passive tissue, and 750 N m/rad at the ankle is close to the M*g*h a standing human
  holds.

Regenerated model: **92 % of noisy resets hold for 10 s**, centre of mass settling at 37 % of the foot.

## 2.5 And the policy's own exploration noise knocks it over anyway

Batch H trained the regenerated body on the best reward configuration. It fell exactly as before —
`fall_rate` 1.000, `ep_len_s` around 1.0 s. Fixing the body was necessary and it was not sufficient,
and `tools/noise_robust.py` says why. This holds the stand pose while injecting the action noise PPO
actually samples, at the standard deviations these runs actually operate at:

| model | act std 0.0 | 0.15 | 0.30 | 0.45 |
|---|---:|---:|---:|---:|
| old body, action_scale 0.5 | 0 % | 0 % | 0 % | 0 % |
| new body, action_scale 0.5 | 88 % | **0 %** | 0 % | 0 % |
| new body, action_scale 0.25 | 88 % | 70 % | 0 % | 0 % |
| new body, action_scale 0.167 | 88 % | **88 %** | 32 % | 0 % |

Every run in this project ends with an action standard deviation between **0.31 and 0.45**, and
starts at `init_std` **0.8**. At 0.8 with `action_scale` 0.5 the sampled action moves every one of 21
joint targets by roughly +-0.4 rad at 50 Hz. **Training opens by shaking the athlete apart**, and the
policy's first task is not to run — it is to cancel its own exploration.

This also explains E4 from the previous session, which lowered `--entropy-coef` 0.005 -> 0.001 and was
the single best change measured there. It was not buying exploitation over exploration in the usual
sense. It was turning down the thing that was knocking the body over.

`--action-scale` and `--init-std` are now trainer flags rather than buried defaults.

## 2.6 Batch H — the physics fix alone makes it worse, and the walk-first curriculum works

All against `f2_trackvar_fall` (old body), 220 iterations, 4096 environments.

| KPI | f2 (old body) | h1 new body | h2 new body + curriculum | h3 new body + init speed |
|---|---:|---:|---:|---:|
| `surv_ratio` | 0.113 | 0.071 | **0.288** | 0.046 |
| `ep_len_s` | 2.26 | 1.41 | **5.75** | 0.91 |
| `v_toward` | 0.810 | 1.134 | 0.751 | 1.017 |
| `v_err` | 2.69 | 2.37 | **0.408** | 2.48 |
| `v_hit_frac` | 0.000 | 0.001 | **0.148** | 0.000 |
| `torque` | 21.7 | 28.9 | 37.2 | 31.7 |
| `power` | 628 | 1100 | 1522 | 1280 |
| `roll_dev` | 9.9 | 15.2 | 13.9 | 16.3 |

**h1 — the body fix on its own is a regression.** Survival falls 0.113 -> 0.071 and torque, power,
jerk and roll all get worse by 30-75 %. This is the noise measurement in Section 2.5 playing out:
tripling `kp` triples the restoring torque that holds the pose *and* triples the torque the
exploration noise injects, and at `action_scale` 0.5 the second effect wins. A stiffer body shaken by
the same noise is shaken harder. Batch I addresses that directly and it is the reason `--action-scale`
now exists.

**h3 — starting episodes already moving does not help** (`surv_ratio` 0.046, the worst of the four).
Reference-state initialisation works when the states it seeds are states the final gait passes
through; a standing pose travelling at 2 m/s is not one of those, it is a shove. The knob stays,
defaulted off.

**h2 — the walk-first curriculum is the first thing to move the velocity KPIs at all.** Survival more
than doubles against the control, episodes run 5.7 s instead of 2.3, and `v_hit_frac` is **0.148**
against 0.000 in every run this project has ever recorded. `v_err` 0.408 is within touching distance
of the 0.35 threshold. Two caveats stated plainly: the target it is tracking is a 1.0 m/s walk, not
the 3.5 m/s goal, and the curriculum never ramped, because ramping is gated on `fall_rate < 0.05` and
`fall_rate` is still 0.999. This is a walk being learned, not a run.

The contrast with g1 is the useful part. g1 (old body, fixed 1.5 m/s target) survives **16.4 s** and
moves at **0.09 m/s** — a statue. h2 (new body, curriculum from 1.0 m/s) survives **5.7 s** and moves
at **0.75 m/s**. g1 scores better on survival and has learned nothing; h2 is the one going somewhere.
This is exactly the trap Section 1.5 of this log was written about, and it is why `v_hit_frac` and
`v_err` are graded alongside `surv_ratio` rather than after it.

## 2.7 Batch I — turning the exploration noise down, and the survival threshold falls

Same reward as batch H's control, on the regenerated body, 220 iterations, 4096 environments. The
only changes are `--init-std` 0.8 -> 0.25 and `--action-scale`.

| KPI | threshold | h2 (scale 0.5) | i1 scale 0.167 | i2 scale 0.25 | i3 0.167 + curriculum |
|---|---|---:|---:|---:|---:|
| `surv_ratio` | > 0.90 | 0.288 | **0.925 PASS** | 0.905 PASS | 0.707 |
| `fall_rate` | < 0.10 | 0.999 | **0.124** | 0.127 | 0.411 |
| `ep_len_s` | | 5.75 | **18.5** | 18.1 | 14.2 |
| `foot_slip` | < 0.15 | 0.443 | **0.112 PASS** | 0.162 | 0.115 PASS |
| `pitch_dev` | < 15 | 5.35 | **3.24 PASS** | 5.33 PASS | 2.31 PASS |
| `roll_dev` | < 10 | 13.9 | **3.32 PASS** | 2.68 PASS | 2.34 PASS |
| `torque` | | 37.2 | **13.6** | 16.9 | 12.1 |
| `power` | | 1522 | **97** | 160 | 91 |
| `jerk` | | 12100 | **2150** | 3042 | 2384 |
| `v_toward` | | 0.751 | **0.006** | 0.013 | 0.012 |
| `v_hit_frac` | > 0.50 | 0.148 | **0.000** | 0.000 | 0.001 |

**`surv_ratio` 0.925 against a threshold of 0.90.** Episodes run 18.5 s of a 20 s limit. Five of the
ten convergence KPIs pass at once, for the first time since this log was started. Torque is down 63 %,
power down 94 %, jerk down 82 %, and `act_sat` is exactly 0.

Confirmed outside the trainer: `eval_100m.py` drives the saved checkpoint in plain MuJoCo, 6 runs,
and it stays upright for the full 20 s in **6 of 6** — the training metric is honest.

It also travels **0.2 m** in those 20 seconds, at a peak of 0.24 m/s. `v_toward` 0.006,
`duty_factor` 0.999. It is a statue, and a very well-behaved one.

So the picture inverts. For the whole of this log the problem was "it falls over"; that problem is
now solved, and the residue is the one underneath it that the falling was hiding: **standing still
pays and moving does not.** Section 2.8 is about that.

One note on `i2` against `i1`: halving the action scale rather than cutting it to a third costs
little in survival (0.905 vs 0.925) and is measurably worse everywhere else (`foot_slip` 0.162 vs
0.112, `jerk` 3042 vs 2150). `i3` shows the walk-first curriculum *costs* survival on top of the
noise fix (0.707 vs 0.925) while buying no speed, because at this stage the curriculum never ramps —
it is gated on `fall_rate < 0.05` and `fall_rate` is 0.41.

## 2.8 Batches J and K — the two halves of the answer, and neither together

**J: cutting the posture income does not help, because r_track takes over as the free income.**
`--posture-w` 1.0 -> 0.3 drops alive + upright + heading from 0.789 per step to 0.237. The athlete
stayed a statue (`v_toward` 0.038 at best), and the per-term accounting says exactly why:

| standing still earns | j1 (track_var 2.0) | j2 (track_var 8.0) |
|---|---:|---:|
| `rt_alive` + `rt_upright` + `rt_heading` | 0.19 | 0.19 |
| `rt_track` | **0.939** | **1.328** |

`r_track` is a Gaussian of variance `track_var` about the commanded speed, so against a 1.0 m/s
command a body that never moves scores `1.5 * exp(-1/8) = 1.33` of the 1.5 on offer. The kernel was
widened from 2.0 to 8.0 to give the reward a gradient at zero speed against a **3.5 m/s** target,
which it does; at 1.0 m/s the same width pays 89 % of the tracking reward for not moving. Cutting the
posture income just moved the free lunch to a different term.

**K: narrowing the kernel makes it move, and it falls again.** `track_var` 0.25 pays a standing body
`1.5 * exp(-4) = 0.03`, and the linear `r_prog` carries the low-speed gradient instead.

| KPI | i1 (var 8) | k1 (var 0.5) | k2 (var 0.25) |
|---|---:|---:|---:|
| `surv_ratio` | **0.925** | 0.079 | 0.071 |
| `v_toward` | 0.006 | 0.620 | **0.677** |
| `v_err` | 3.494 | 0.416 | **0.361** |
| `v_hit_frac` | 0.000 | 0.163 | **0.284** |
| `air_time` | 0.027 | 0.118 | **0.094 (in band)** |
| `ep_len_s` | 18.5 | 1.58 | 1.41 |

`v_err` 0.361 against a 0.35 threshold and `v_hit_frac` 0.284 are both far and away the best this
project has measured. And the episode is 1.4 s.

**L: pricing the fall higher does not fix it.** `--fall-penalty` 5 -> 25 -> 50 on k2's settings, and
`fall_rate` stayed at 1.000 in all three. Stopped at ~115 iterations once that was unambiguous.

## 2.9 What the "fast" policy is actually doing: falling toward the target

`tools/gait_trace.py` dumps one episode at the control rate rather than a summary. On k2:

```
     t       x       y  pelv_z       v  feet
  0.34  -0.037  -0.121   0.887   -0.27  LR
  0.42  -0.063  -0.151   0.892   -0.41  -R     <- left foot leaves the ground
  0.74  -0.269  -0.366   0.839   -0.69  -R
  1.06  -0.481  -0.608   0.653   -0.63  -R
  FELL at t=1.18 s
footfalls: left 0, right 0 over 1.18 s
```

**Zero footfalls.** It lifts one foot, never puts it down, and travels 0.68 m/s while its pelvis
sinks from 0.90 m to 0.57 m. It is not walking badly. It is not walking at all — it is falling over,
and the direction it falls in happens to be the direction of its target.

`r_track` and `r_prog` read centre-of-mass velocity and do not ask how it was produced. For a
standing body, **toppling is by far the cheapest way to acquire horizontal speed**, and the reward
pays for it at exactly the rate it pays for a walk, for the entire 1.5 s the fall takes. The fall
penalty arrives once, at the end, which is why raising it from 5 to 50 changed nothing: it is
competing against 75 steps of paid falling.

This is the same class of error as the two the previous session found, and it explains every "moves
but falls" result in this log:

- **E1's `r_air`**: the cheapest way to be off the ground is to fall over.
- **E1b's `r_slip`**: a reset artefact, not sliding.
- **This**: the cheapest way to have forward velocity is to fall over.

Each time, a term that names the right quantity was measuring it in a state where the quantity means
something else. The lesson AGENTS.md already records — *check what a metric counts before trusting
it* — applies to reward terms exactly as it does to readouts, and a per-term accounting does not
catch this one. `rt_track` looked healthy. Only the per-step trace showed there were no footfalls.

**The fix under test (batch M): `--vel-gate`.** Both velocity terms are multiplied by a factor that
is 1 while the pelvis is above 95 % of stand height and uprightness is above 0.95, and falls to 0 by
85 % / 0.85. Both bounds sit far outside normal gait variation and far inside the termination test
(60 % of stand height, upright 0.4), so a real stride pays nothing for it and a topple stops earning
in its first tenth of a second.

## 2.10 Batch M — the gate works, and reveals that the athlete has no way to discover stepping

`--vel-gate 1.0` on k2's settings. Stopped at ~145 of 400 iterations once the direction was clear.

| KPI @ ~145 | k2 (no gate) | m1 gate + fall 25 | m2 gate + fall 5 | m3 gate + 48-step horizon |
|---|---:|---:|---:|---:|
| `surv_ratio` | 0.071 | **0.389** | 0.083 | **0.436** |
| `v_toward` | 0.677 | 0.072 | 0.576 | 0.055 |

The gate does what it was built to do. m1 and m3 stop toppling — survival rises five-fold — and the
athlete goes straight back to standing still. m2, which kept the low fall penalty, still topples:
the gate removes the *payment* for a fall but a 5.0 termination cost still leaves it cheap.

Doubling the GAE horizon (24 -> 48 steps, m3) helps survival about as much as the gate does and is
worth keeping, but it does not produce movement either.

So the exercise arrives at a clean statement of the real problem. With the body fixed and the topple
no longer paid for, the athlete has **two reachable behaviours and no path between them**: stand
still, or fall over. It has never once taken a step, in any run in this log.

## 2.11 Batch N — the thing that was missing: the policy had no stride to be in

The policy is a feedforward MLP. Walking is a *periodic* behaviour, and nothing in the 78-float
observation says where in a stride the body is. It can in principle infer phase from its own leg
angles, but nothing in the task says a stride is a thing that exists, and the reward terms that
mention the feet (`r_air`, `r_alt`, `r_clear`) only pay *after* a footfall has happened — they can
refine a gait, they cannot get one started.

That is the gap every result above has been circling, and it is what periodic reward composition
(Siekmann et al.) exists to close. `--gait-w` adds both halves at once:

- **A clock in the observation** — `sin`, `cos` of a phase advancing once per control step over a
  `--gait-period` (0.8 s) cycle. Observation goes 78 -> 80.
- **A signed reward for matching a walking contact schedule** — the left foot asked to be in stance
  for the first `--gait-duty` (0.6) of the cycle, the right the same window half a cycle later, so
  20 % of the stride is double support. `+1` per foot where the schedule asks, `-1` per foot where it
  does not. A body standing with both feet planted scores `2 * (2 * 0.6 - 1) = +0.4` of the `+2.0` a
  correct alternating gait earns — verified against the implementation at 0.198 vs 0.200 predicted
  with `gait_w` 0.5 — so **stepping is worth five times standing still**.

Also added and used from this batch on: `--spawn-facing`, which starts an episode with the athlete
already pointed at its target. It otherwise spawns at a uniformly random yaw with its target at a
uniformly random bearing, so the expected angle between them is 90 degrees and a quarter of episodes
open facing backwards — meaning turning on the spot has to be learned before walking pays anything.
Mid-episode targets are still re-rolled to a random bearing, so the skill is still required, just not
before the first step.

**Batch N result: it declined the offer.** Stopped at ~93 of 400 iterations once all three arms had
settled.

| KPI @ ~93 | n1 gait 0.5 | n2 gait 1.0 | n3 gait 1.0, 1.0 s stride |
|---|---:|---:|---:|
| `surv_ratio` | 0.157 | **0.814** | 0.469 |
| `v_toward` | 0.123 | -0.004 | -0.011 |
| `duty_factor` | 0.989 | **0.9988** | 0.9987 |
| `rt_gait` | 0.207 | **0.3995** | 0.3985 |

`rt_gait` 0.3995 against a predicted statue floor of `2 * (2 * 0.6 - 1) = 0.400`. To four figures,
n2 is collecting the exact amount a body with both feet planted collects and **not one step above
it**. Traced through `gait_trace.py`, the saved policy holds its pelvis at 0.887 m for four seconds
with both feet down and zero footfalls — a better statue than i1, and still a statue.

So the gait clock is not sufficient either, and the reason is an economic one rather than a
representational one. Standing still banks about 0.57 per step for a thousand steps. A correct stride
is worth +1.6 per step more than that — but reaching it means balancing on one leg for 0.32 s, which
takes a lateral weight shift over the stance foot, and **one failed attempt ends the episode**. With
`--fall-penalty` at 25 and exploration at `init_std` 0.25, the policy will not buy a lottery ticket
that expensive.

Every earlier attempt to make falling cheap produced toppling instead of stepping, because a topple
toward the target was paid like a walk toward it. Two things now block that: `--vel-gate` stops the
velocity terms paying a body on its way down, and the gait reward cannot be collected by falling at
all — it pays only for feet that are where a walking schedule asks, and a body on the floor has both
feet in contact at all the wrong times. **For the first time in this log, exploration can be made
cheap without opening an exploit**, which is what batch O tests.

## 2.12 Batch O — the athlete takes its first steps

Falls made cheap (`--fall-penalty` 2.0) and exploration turned back up (`--init-std` 0.5,
`--entropy-coef` 0.01), on top of the gait clock and the velocity gate. Stopped at iteration 97 when
the clock ran short; every number below was still moving in the same direction.

| iteration | 4 | 23 | 44 | 69 | 97 |
|---|---:|---:|---:|---:|---:|
| `rt_gait` (o2, floor 0.80, max 4.00) | 0.72 | 1.05 | 1.93 | 2.63 | **3.37** |
| `duty_factor` (o2) | 0.937 | 0.875 | 0.754 | 0.689 | **0.621** |
| `rt_gait` (o1, floor 0.40, max 2.00) | 0.36 | 0.50 | 0.91 | 1.33 | **1.62** |
| `v_toward` (o1) | -0.17 | 0.09 | 0.24 | 0.32 | **0.47** |

**This is the first stepping in this project's history.** `rt_gait` goes from the statue floor to
84 % of a perfect alternating gait, and `duty_factor` from 0.999 — both feet planted, permanently —
to 0.62, which is inside the human walking band. The athlete is putting one foot down at a time, in
time with the clock.

The three changes that made it possible are not independent, and none of them works alone:

1. **The body can stand** (Section 2.4), so a step is a thing it can attempt.
2. **The exploration no longer knocks it over** (Section 2.5), so an attempt is not immediately
   destroyed by noise.
3. **Falling is cheap *and* cannot be profitable.** Every previous attempt at (3) produced toppling,
   because a topple toward the target was paid like a walk toward it. `--vel-gate` closes that, and
   the gait reward cannot be farmed by falling either. Cheap falls plus a closed exploit is what
   turns "do not risk it" into "try it".

`o1` (gait weight 1.0) and `o2` (gait weight 2.0) reach the same fraction of a perfect gait, but
`o1` also moves at 0.47 m/s where `o2` sits at 0.25: doubling the gait reward drowns out the tracking
and progress terms, and **marching on the spot collects it just as well as walking does**. The gait
reward has to stay small enough that going somewhere still matters.

All three arms fall every episode, at 1.8-2.3 s. That is what cheap falls buy, and it is the point of
stage two.

## 2.13 Batch P — stage two: keep the stride, stop the falling

Resumed from o1 with the two settings that made trying a step affordable reversed —
`--fall-penalty` 2 -> 10 / 25, `--entropy-coef` 0.01 -> 0.003 / 0.001 — and nothing else changed.

**It walks.** Progress from the resumed base (iteration 50, a half-formed stride that fell every
episode) over the following ~150 iterations:

| iteration | 132 | 148 | 154 | 183 | 197 |
|---|---:|---:|---:|---:|---:|
| `surv_ratio` (p1) | 0.146 | 0.248 | 0.317 | **0.928** | **0.950** |
| `fall_rate` (p1) | 1.000 | 1.000 | 0.999 | 0.218 | **0.133** |
| `ep_len_s` (p1) | 2.93 | 4.96 | 6.35 | 18.57 | **19.00** |
| `v_toward` (p1) | 0.527 | 0.595 | 0.592 | 0.538 | **0.567** |
| `duty_factor` (p1) | 0.624 | 0.615 | 0.610 | 0.601 | **0.596** |

Survival and locomotion at the same time, which no run in this log had ever produced. And unlike
every previous "survival" result, `duty_factor` is 0.60 rather than 0.999 — it is not standing still.

Traced step by step (`tools/gait_trace.py`, 8 s):

```
  6.50   2.569  -1.495   0.884    0.09  LR
  6.62   2.593  -1.524   0.900    0.34  L-
  6.74   2.649  -1.570   0.895    0.51  L-
  6.86   2.707  -1.619   0.884    0.49  LR
  6.98   2.749  -1.658   0.904    0.26  -R
  7.10   2.782  -1.724   0.905    0.28  -R
footfalls: left 10, right 10 over 8.00 s
```

Ten footfalls on each side, strictly alternating, with the pelvis holding 0.88-0.91 m against a stand
height of 0.902 — and the `L- / LR / -R / LR` cycle is single support, double support, single
support: **a walk, with the double-support phase a walk is supposed to have.**

Confirmed outside the trainer on the iteration-150 checkpoint, `eval_100m.py`, 8 runs from different
seeds:

```
run 0: fell        2.22s    -0.9 m
run 1: timed out  30.00s    15.7 m    peak 1.07 m/s
... (runs 2-7 identical to within 0.4 m)
mean distance 13.5 m, mean upright 26.5 s
```

**Seven of eight walk for the full thirty seconds and cover 15.5 m** at a sustained 0.52 m/s. The
training metric and the independent rollout agree.

### A KPI that no real gait could ever have passed

`air_time`'s convergence threshold in Section 1.7 is **0.05-0.25 s**, justified there as "a human
sprint stride flies 0.10-0.20 s". The walking policy measures **0.30 s** and fails it.

The threshold is wrong, and in the way this log keeps finding. Whole-body flight time -- the part of
a sprint where neither foot is down -- is indeed 0.10-0.20 s. But the metric does not measure that.
`air_sum` accumulates `prev_air` at each first contact, which is **how long that foot was off the
ground**: swing time, per foot, not flight time for the body. A human walking swings each leg for
about 0.4 s and a sprinter for about 0.35 s, so **no real gait of any kind can score inside the
0.05-0.25 band**, and the baseline's 0.61 was never the "not a stride" evidence Section 1.5 read it
as -- only its size was.

The commanded gait settles it arithmetically. `--gait-period` 0.8 s with `--gait-duty` 0.6 asks each
foot to swing for `(1 - 0.6) * 0.8 = 0.32 s`, and the policy measures 0.30. It is doing exactly what
it was asked, to within 6 %.

Corrected band for this metric, as swing time: **0.25-0.50 s**, and the walking policy passes it.
`duty_factor` remains the honest gait discriminator, and it reads 0.59.

---

# Session 2 results

## What was actually wrong, in order of how much it mattered

1. **The body could not stand up.** Holding its own stand keyframe with zero action, the athlete
   toppled backwards and was on the floor in **1.75 s** — the episode length every run in this
   project had ever reported. Under the trainer's own reset noise, **0 of 40 trials survived 5 s**.
   Two causes: a stand pose whose centre of mass sat at 17 % of the heel-to-toe span (a standing
   human is near 45 %), and joint gains below the `M*g*h` = 662 N m/rad stability floor for an
   inverted pendulum, with the ankle shipping at 250. Fixed in the generator: a 3 degree forward lean
   and 3x the leg and trunk stiffness, force and velocity limits untouched. **92 % of noisy resets now
   hold for 10 s.**
2. **The policy's own exploration noise knocked the fixed body over.** At `init_std` 0.8 and
   `action_scale` 0.5 the sampled action moves all 21 joint targets by about +-0.4 rad at 50 Hz.
   Measured: 0 % survive 5 s at every standard deviation these runs actually operate at. Training
   opened every run by shaking the athlete apart. At `action_scale` 0.167 and `init_std` 0.25,
   survival went from 0.29 to **0.925** in 220 iterations.
3. **A topple toward the target was paid exactly like a walk toward it.** `r_track` and `r_prog` read
   centre-of-mass velocity and do not ask how it was produced; toppling is the cheapest way for a
   standing body to acquire horizontal speed. The fastest policy in this project covered 0.68 m/s and
   took **zero footfalls**. `--vel-gate` fades both terms out as the body drops or tilts.
4. **The policy had no stride to be in.** A feedforward MLP was being asked for a periodic behaviour
   with nothing in its 78 observations saying where in a stride it was, and the terms that mention the
   feet only pay *after* a footfall — they can refine a gait, they cannot start one. `--gait-w` adds a
   clock to the observation and a signed reward for matching a walking contact schedule.
5. **Trying a step was unaffordable.** Even with the clock, the athlete sat at `rt_gait` 0.3995
   against a statue floor of 0.400 — not one step above it — because one failed attempt ended a
   1000-step episode. Only once (3) and (4) made falling *unprofitable* could falling be made *cheap*
   without opening an exploit. Cheap falls plus real exploration is what produced the first steps.
6. **The tracking kernel's width was tuned for the wrong target speed.** Widened to 8.0 to give a
   gradient at zero speed against a 3.5 m/s target, it pays a standing body 89 % of the tracking
   reward at a 1.0 m/s target. Narrowed to 0.25, with the linear progress term carrying the low-speed
   gradient instead.
7. **The speed curriculum was built to ramp the wrong way.** `--target-speed` is the *floor* of the
   adaptive ramp and every run had set it to 3.5, so the curriculum could only ever ramp upward from
   a sprint. There had never been a walk-first curriculum; the flag already existed.
8. **Two `air_time` mistakes.** The metric measures per-foot swing time, not whole-body flight time,
   so its 0.05-0.25 s threshold was unreachable by any real gait. Corrected to 0.25-0.50 s.

## Measured and rejected

- **The double-support penalty is not what stops it walking.** Removing `r_alt` entirely changes
  nothing (`surv_ratio` 0.099 vs 0.113).
- **Resetting episodes already moving makes things worse** (`surv_ratio` 0.046, worst of its batch).
  A standing pose travelling at 2 m/s is a shove, not a state the final gait passes through.
- **Raising the fall penalty does not stop a topple** (5 -> 25 -> 50, `fall_rate` 1.000 throughout).
  The topple is paid for the whole 1.5 s it lasts; the penalty arrives once at the end.
- **`action_scale` 0.167 does not prevent a stride.** Open-loop, the swing foot still clears 0.20 m
  against the 0.05-0.10 m a walk needs.
- **Widening the tracking kernel does not raise speed** (f1: `v_toward` 0.440 against the control's
  0.495), contrary to the commit that introduced it.

## Two process failures fixed

- **Runs did not record their own arguments.** Two three-hour runs left a CSV, a log and a
  TensorBoard directory between them and not one setting, so their target speed had to be recovered
  from event files. Now `logs/<run>.args.json`.
- **Checkpoints were not namespaced per run**, so short A/Bs overwrote long runs' weights and
  concurrent runs interleaved their saves. Now `checkpoints/<task>/<run>/`, and a checkpoint carries
  the `action_scale` it was trained at.

Also recorded, because it cost a policy: **the foot-contact fix invalidated every checkpoint trained
before it.** It changed two of the 78 observations, so `overnight_3h` — 1.4 billion steps, the best
result the project had — now falls after 1.5 s when driven through the current rig.

## Final result

`p1_soft`, iteration 287 (stopped by the clock, still improving), graded over its last 20 iterations.

| KPI | threshold | session start (baseline) | **final** | |
|---|---|---:|---:|---|
| `surv_ratio` | > 0.90 | 0.080 | **0.986** | PASS |
| `fall_rate` | < 0.10 | 1.000 | **0.055** | PASS |
| `v_err` | <= 0.35 | 2.42 | **0.344** | PASS |
| `duty_factor` | 0.35-0.65 | 0.259 | **0.578** | PASS |
| `pitch_dev` | < 15 deg | 10.0 | **2.39** | PASS |
| `air_time` (corrected band, see above) | 0.25-0.50 s | 0.614 | **0.333** | PASS |
| `v_hit_frac` | > 0.50 | 0.000 | 0.251 | fail |
| `foot_slip` | < 0.15 m/s | 0.259 | 0.309 | fail |
| `roll_dev` | < 10 deg | 15.1 | 12.5 | fail |
| `ep_len_s` | (20 s episode) | 1.60 | **19.72** | |

**Six of ten, from none.** And independently, outside the trainer, on the saved checkpoint
(`eval_100m.py`, ten runs from ten seeds, 30 s each — half again the training episode):

```
run 0..9: timed out  30.00s   25.5-26.5 m   peak 1.36-1.39 m/s
10/10 upright for the full 30 s, mean distance 26.0 m, mean peak 1.38 m/s
```

**Ten consecutive evaluation episodes, zero falls**, each covering 26 m at a sustained 0.87 m/s.
`gait_trace.py` over ten seconds counts **12 left footfalls and 12 right**, strictly alternating, at a
0.83 s stride against the 0.8 s commanded, with the pelvis holding 0.88-0.92 m throughout.

The athlete walks.

## Exit criteria — which one fired

| Exit condition | Status |
|---|---|
| All KPIs met over 10 consecutive evaluation episodes | **partly** — 10/10 episodes upright with zero falls, but 3 of 10 KPIs still short |
| Improvement plateau < 3 % over 3 runs | **not met** — `v_toward` rose 0.55 -> 0.79 over the final 60 iterations |
| Time limit | **fired** |

The run was still improving monotonically on every headline number when the clock stopped it, at
**287 iterations of a task this project budgets 2000-2500 for**. Nothing here has converged; what has
happened is that the failure modes blocking convergence have been removed.

## What is left, in priority order

1. **Just run it longer.** `p1_soft` was stopped at 287 iterations by the clock, not by a plateau.
   `v_toward` was rising 0.55 -> 0.79 across its last 60 iterations and `v_hit_frac` 0.15 -> 0.25.
   Resume it and the speed KPIs are the ones that move.

   ```
   cd training
   .venv/Scripts/python.exe train_run.py --task target --num-envs 8192 --iters 2500        --resume checkpoints/run_to_target/p1_soft/latest.pt        --track-var 0.25 --prog-w 0.75 --posture-w 0.3 --target-speed 1.0        --target-speed-final 3.5 --speed-adaptive        --vel-gate 1.0 --spawn-facing 1.0 --gait-w 1.0        --action-scale 0.167 --entropy-coef 0.003 --fall-penalty 10.0
   ```

   `--speed-adaptive` is now worth setting: it ramps when `fall_rate < 0.05`, and `fall_rate` is
   0.055 and falling, so the walk-to-run curriculum will actually start. Note `--target-speed` is the
   ramp's **floor** — 1.0, not 3.5.

2. **`foot_slip` 0.31 against a 0.15 threshold** is the largest remaining gait defect and the most
   likely thing holding `v_hit_frac` down: a foot that slides under load cannot push. Worth
   raising `r_slip`'s weight now that the gait exists to be refined, which was never true before.

3. **`roll_dev` 12.5 degrees** — the walk rolls side to side more than it should. `r_ang` and
   `r_width` are the levers, and `--gait-duty` slightly higher would lengthen double support.

4. **The action standard deviation is still 0.47.** `--entropy-coef` 0.003 held it up to keep the
   stride forming; with the stride established, annealing it toward 0.15 should improve slip, roll
   and jerk together — Section 2.5 measured 88 % survival at std 0.15 against 32 % at 0.30.

5. **Unity transfer is not done.** The MJCF gains changed, so `Athlete_PolicyConfig`'s Kp/Kd must be
   brought into step with `models/athlete_policy_config.json`, and the observation is now **80**
   floats, not 78 — the two extra are the gait clock, `sin` and `cos` of a phase advancing once per
   50 Hz control step over 0.8 s. `PolicyRunner` validates observation width and will log an error
   rather than fail silently, but nothing on the Unity side generates that clock yet.

## Throughput and hardware efficiency

Unchanged from Section 1.6 and re-confirmed: ~177k steps/s for one 8192-environment run with the GPU
to itself, ~68k each for two concurrent 4096-environment runs (136k aggregate), ~44k each for three
(132k aggregate). Running experiments **concurrently costs about 25 % of aggregate throughput and
buys three controlled answers in the wall-clock time of one**, which for A/B work is the right trade;
the final long runs used two at a time. Nothing in this session was throughput-limited — every
conclusion came from 220-400 iteration runs of 10-25 minutes.

---

## TL;DR

The athlete could not stand: it fell over in 1.75 s under its own weight, which was every run's
episode length. Fixed the body, the exploration noise, and a reward that paid for falling. It walks.
