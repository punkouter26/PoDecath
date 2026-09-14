# Batch L - cross the two halves of the answer.
#
# The exercise now has both halves and neither together:
#
#   i1 (wide kernel)   surv_ratio 0.925, v_hit_frac 0.000  - stands for 18.5 s and goes nowhere
#   k2 (narrow kernel) surv_ratio 0.071, v_hit_frac 0.284  - moves at 0.68 m/s and falls at 1.4 s
#
# k2's v_err is 0.361 against a 0.35 threshold and its air_time 0.094 s is inside the human band, so
# the speed half of the problem is close. What it does not do is stay up. The fall penalty is the
# knob that prices that, and the k series ran at 5.0 - chosen back when the body physically could not
# stand and a large penalty only taught it not to try.
#
# 400 iterations rather than 220: both behaviours above were fully formed by 220, so a longer window
# is the only way to see whether a middle one exists or whether the reward just has two attractors.
$root = "C:\Users\punko\Downloads\PoDecath\training"
$launch = Join-Path $root "tools\launch_run.ps1"

$common = @("--task","target","--num-envs","4096","--iters","400",
            "--entropy-coef","0.001","--init-std","0.25","--action-scale","0.167",
            "--track-var","0.25","--prog-w","0.75","--posture-w","0.3",
            "--keep-old-runs","--no-tensorboard")

& $launch -RunName "l1_fall25"   @common --target-speed 1.0 --fall-penalty 25.0
& $launch -RunName "l2_fall50"   @common --target-speed 1.0 --fall-penalty 50.0
& $launch -RunName "l3_slow"     @common --target-speed 0.7 --fall-penalty 25.0
Write-Host "batch L launched"
