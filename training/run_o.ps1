# Batch O - make trying a step cheap, now that falling cannot be profitable.
#
# Batch N gave the policy a stride to be in and it declined to use it: n2 settles at surv_ratio 0.81
# with duty_factor 0.999 and rt_gait 0.3995, which is 2*(2*duty-1) to four figures -- exactly the
# floor a statue collects and not one step above it. A correct gait is worth +2.0 against that +0.4,
# so the pull exists; what is missing is any reason to risk finding it. Standing still banks ~0.57
# per step for 1000 steps, and one failed attempt at a step ends the episode.
#
# Every previous attempt to make exploration cheaper (lowering --fall-penalty) produced toppling,
# because a topple toward the target was paid like a walk toward it. --vel-gate closed that, and the
# gait reward cannot be collected by falling either: it pays only for feet that are where a walking
# schedule asks, and a body on the floor has both feet in contact at the wrong times. So for the
# first time in this log, falls can be made cheap and exploration turned up without opening an
# exploit.
#
#   o1 - cheap falls (2.0) and real exploration (init_std 0.5, entropy 0.01), gait weight 1.0.
#   o2 - the same with the gait reward doubled, so a step is worth +4.0 against a statue's +0.8.
#   o3 - a middle setting with more joint travel per unit action (0.25 rather than 0.167).
$root = "C:\Users\punko\Downloads\PoDecath\training"
$launch = Join-Path $root "tools\launch_run.ps1"

$common = @("--task","target","--num-envs","4096","--iters","400",
            "--track-var","0.25","--prog-w","0.75","--posture-w","0.3",
            "--target-speed","1.0","--vel-gate","1.0","--spawn-facing","1.0",
            "--keep-old-runs","--no-tensorboard")

& $launch -RunName "o1_explore" @common `
    --action-scale 0.167 --init-std 0.5 --entropy-coef 0.01 --fall-penalty 2.0 --gait-w 1.0
& $launch -RunName "o2_explore_gait2" @common `
    --action-scale 0.167 --init-std 0.5 --entropy-coef 0.01 --fall-penalty 2.0 --gait-w 2.0
& $launch -RunName "o3_scale25" @common `
    --action-scale 0.25 --init-std 0.35 --entropy-coef 0.005 --fall-penalty 2.0 --gait-w 2.0
Write-Host "batch O launched"
