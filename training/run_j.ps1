# Batch J - now that the athlete can stay up, make standing up not be the answer.
#
# Batch I fixed survival by turning down the exploration noise (action_scale 0.5 -> 0.167) and the
# athlete promptly went back to standing still, as it has every time it has been able to. Two things
# pay it to do that, and this batch separates them.
#
# 1. Posture income. alive + upright + heading pay 0.789 per step for existing in good posture,
#    against 0.008 for progress and tracking combined on the 3-hour run. --posture-w scales the
#    first without touching what running is worth.
#
# 2. The tracking kernel is too wide for a walk-first curriculum. r_track is a Gaussian of variance
#    track_var about the commanded speed, and the width that gives a useful gradient at a 3.5 m/s
#    target is far too generous at 1.0 m/s: with track_var 8, standing still scores
#    1.5 * exp(-1/8) = 1.32 out of 1.5, so the whole reward for hitting the commanded walk is 0.18.
#    The earlier fix for "no gradient at zero speed" was aimed at a fixed 3.5 m/s target and quietly
#    breaks the curriculum it now has to share the reward with. r_prog is linear in speed and is the
#    term that should carry the low-speed gradient, so this narrows the kernel and leans on that.
#
#   j1 - narrow kernel (2.0), progress weight up (0.5), posture income at 0.3.
#   j2 - the same but the wide kernel kept, to isolate the kernel from the posture change.
#   j3 - the same as j1 but posture income at 0.6, to isolate the posture change from the kernel.
#
# All three carry batch I's action_scale 0.167 and the walk-first curriculum (1.0 -> 3.5 adaptive).
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--entropy-coef","0.001","--fall-penalty","5.0",
            "--init-std","0.25","--action-scale","0.167",
            "--target-speed","1.0","--target-speed-final","3.5","--speed-adaptive",
            "--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","2.0","--prog-w","0.5","--posture-w","0.3","--run-name","j1_narrow_post03")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","8.0","--prog-w","0.5","--posture-w","0.3","--run-name","j2_wide_post03")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","2.0","--prog-w","0.5","--posture-w","0.6","--run-name","j3_narrow_post06")) -NoNewWindow
Write-Host "batch J launched"
