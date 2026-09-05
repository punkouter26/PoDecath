# MuJoCo Warp vs Isaac Lab on the same athlete

Both trainers learned the same task on the same body: Matt's skeleton converted to a 21-joint model, identical
masses, joint limits, PD gains and torque limits (verified by matching a hip step response to within 1 %
and body orientations for every joint to 3 decimals), the same 75-float observation, the same reward terms,
and PPO with the same hyper-parameters. The exported policies drop into Unity's `PolicyRunner` unchanged.

Green athlete = MuJoCo Warp policy. Yellow athlete = Isaac Lab policy. Both race in `Assets/Scenes/RooftopLap.unity`.

## Results (2026-09-04)

| | MuJoCo Warp + custom PPO | Isaac Lab (PhysX) + rsl_rl PPO |
|---|---|---|
| Environments | 4096 | 2048 (4096 overflowed 12 GB VRAM with the 23-link chained body) |
| Iterations / control steps | 1500 / 147 M | 1500 / 74 M |
| Wall time | 53.6 min | 40.1 min |
| Throughput | 47.8 k control steps/s (55-60 k early) | 30.8 k control steps/s (56 k with the 13-link D6 body at 4096 envs) |
| Domain randomisation | none | friction, base mass, random pushes, observation noise (Isaac Lab standard) |
| Episodes reaching 18 s | iteration 517 | iteration 1114 (with pushes; 159 without randomisation) |
| Final mean episode length | 19.9 s of 20 | 18.3 s of 20 (episodes cut short by pushes) |
| 100 m in MuJoCo, 3 runs | 3/3 clean, 28.4 s | 3/3 clean, 28.9 s |
| Rooftop lap in Unity, 9 m carrot (real-time play, 5 attempts each) | 23.2 s, 5/5 clean (lap-tuned policy) | 25.7 s, 5/5 clean (no lap tuning) |
| Rooftop lap in Unity, 6 m carrot | 24.9 s, 5/5 clean | 27.9 s once; fell at the first bend (22-23 m) in 3 of 4 attempts |
| Peak speed | ~4.5 m/s | ~4.2 m/s |

## What trained differently

- **Learning curve.** Per iteration, Isaac Lab's rsl_rl learned to stay up faster: 18 s episodes by iteration
  159 when trained without randomisation, versus 517 for the MuJoCo run. With pushes and noise switched on it
  took 1114 iterations, because the task is genuinely harder.
- **Throughput.** On the plain 13-link body Isaac Lab was the faster simulator (56-60 k vs 48-55 k steps/s).
  The corrected 23-link body costs Isaac ~45 % throughput and half the environment count; MuJoCo Warp handles
  multi-joint bodies natively and did not need the chain.
- **Transfer.** The first Isaac policy (13-link D6 import) fell within one second in both MuJoCo and Unity even
  though it ran fine in Isaac. Cause: the MJCF importer maps multi-joint bodies onto a D6 joint with PhysX's
  fixed X/Y/Z axis order, which swapped the abdomen axes and bent the shoulder sideways. After converting the
  chained model, the Isaac policy transferred to both engines on the first try. The MuJoCo policy transferred
  to Unity without any of this, but also had no randomisation and would likely be less robust to pushes.
- **Gait.** Both learned a crouched jog with balance-pole arms; neither reward asks for human form. The Isaac
  policy runs about 8 % slower and more conservatively, consistent with being trained under random pushes.

## Reproduce

```
# MuJoCo Warp
cd training && .venv/Scripts/python.exe train_run.py --num-envs 4096 --iters 1500
# Isaac Lab
cd training/isaac && .venv/Scripts/python.exe -u train_isaac.py --headless --num-envs 2048 --iters 1500
# table from TensorBoard logs
cd training && .venv/Scripts/python.exe compare.py     # writes DOCS/COMPARISON_TABLE.md
```

TensorBoard (both runs): http://localhost:6006
