# Batch N - give the policy a stride to be in.
#
# Everything up to here has asked a memoryless MLP to produce a periodic behaviour with nothing in
# its observation that says where in a stride it is. It can in principle read that out of its own leg
# angles, but nothing tells it a stride exists, and the measured result is that it never takes a
# step: k2 covered 0.68 m/s with **zero footfalls**, and batch M's velocity gate stopped that being
# paid for and sent it straight back to standing still.
#
# --gait-w adds two things at once, which is the point of it:
#   * a clock in the observation (sin, cos of phase; obs 78 -> 80), so the policy can time itself;
#   * a signed reward for having each foot where a walking schedule says it should be. Left foot in
#     stance for the first `gait-duty` of the cycle, right foot the same half a cycle later. At duty
#     0.6 that is 20% double support -- a walk. A statue scores 2*(2*0.6-1) = +0.4 of the +2.0 a
#     correct alternating gait earns, so stepping is worth five times standing still.
#
# Also new in this batch: --spawn-facing 1.0. The athlete otherwise spawns at a random yaw with its
# target at a random bearing, so it has to learn to turn on the spot before walking pays anything.
#
#   n1 - gait weight 0.5
#   n2 - gait weight 1.0
#   n3 - gait weight 1.0 with a 1.0 s stride instead of 0.8 s (slower, longer stance)
$root = "C:\Users\punko\Downloads\PoDecath\training"
$launch = Join-Path $root "tools\launch_run.ps1"

$common = @("--task","target","--num-envs","4096","--iters","400",
            "--entropy-coef","0.001","--init-std","0.25","--action-scale","0.167",
            "--track-var","0.25","--prog-w","0.75","--posture-w","0.3",
            "--target-speed","1.0","--vel-gate","1.0","--fall-penalty","25.0",
            "--spawn-facing","1.0","--keep-old-runs","--no-tensorboard")

& $launch -RunName "n1_gait05" @common --gait-w 0.5
& $launch -RunName "n2_gait10" @common --gait-w 1.0
& $launch -RunName "n3_gait10_slow" @common --gait-w 1.0 --gait-period 1.0
Write-Host "batch N launched"
