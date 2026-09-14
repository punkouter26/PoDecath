# Batch P - stage two: keep the stride, stop the falling.
#
# Batch O did what nothing before it managed. With falls made cheap (--fall-penalty 2.0), real
# exploration (init_std 0.5, entropy 0.01), the velocity gate closing the topple exploit and the gait
# clock paying for correctly timed contact, rt_gait climbed off the statue floor toward 70% of a
# perfect alternating gait and duty_factor fell from 0.999 to 0.69. The athlete started taking steps.
#
# It also falls every episode, which is exactly what cheap falls buy. So this resumes that policy and
# reverses the settings that made trying a step affordable, now that the stride exists and no longer
# has to be discovered:
#
#   --fall-penalty 2.0 -> 15 / 40   falling costs something again (the unknown, so it is bracketed)
#   --entropy-coef 0.01 -> 0.001    the exploration that found the stride is no longer needed
#
# Base is o1 rather than o2: both reached about 70% of a perfect gait, but o1 (gait weight 1.0) also
# moves at 0.34 m/s while o2 (gait weight 2.0) sits at 0.05, because doubling the gait reward drowns
# out the tracking and progress terms and marching on the spot collects it just as well.
#
# --init-std is not passed: a resumed run loads the policy's own log_std from the checkpoint, so the
# standard deviation comes down through the entropy coefficient rather than being reset.
param(
    [string]$Base = "C:\Users\punko\Downloads\PoDecath\training\checkpoints\run_to_target\o1_explore\latest.pt",
    [int]$Iters = 1600
)
$root = "C:\Users\punko\Downloads\PoDecath\training"
$launch = Join-Path $root "tools\launch_run.ps1"

# save-every 25, not 50: batch O was stopped at iteration 97 and its newest checkpoint was 50,
# so half the stride it had learned was not on disk. A run that may be cut short by the clock has
# to checkpoint on the clock's timescale.
$common = @("--task","target","--num-envs","4096","--iters","$Iters","--resume",$Base,
            "--track-var","0.25","--prog-w","0.75","--posture-w","0.3","--target-speed","1.0",
            "--vel-gate","1.0","--spawn-facing","1.0","--gait-w","1.0",
            "--action-scale","0.167","--max-hours","1.0",
            "--save-every","25","--keep-old-runs","--no-tensorboard")

# The base is only at iteration 50, where the stride is half formed, so p1 keeps some exploration to
# finish building it while p2 locks in and leans on the fall penalty.
& $launch -RunName "p1_soft" @common --fall-penalty 10.0 --entropy-coef 0.003
& $launch -RunName "p2_firm" @common --fall-penalty 25.0 --entropy-coef 0.001
Write-Host "batch P launched from $Base to iteration $Iters"
