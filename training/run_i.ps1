# Batch I - stop the exploration noise from knocking the athlete over.
#
# tools/noise_robust.py, on the regenerated body: holding the stand pose with zero action, 88% of
# noisy resets survive 5 s. Add the action noise PPO actually samples at action_scale 0.5 and a
# standard deviation of 0.15 -- well below the 0.31-0.45 every run in this project ends at -- and it
# is 0%. At action_scale 0.167 the same noise leaves 88% standing. The exploration was the thing
# knocking the body down, and action_scale is the gain on it.
#
# All three carry the batch G/H reward settings so the only new variables are the two named.
#
#   i1 - action_scale 0.167, the setting the noise measurement picks.
#   i2 - action_scale 0.25, the same idea applied half as hard, to see the trade against reachable
#        joint travel (+-0.75 rad of target against +-0.5).
#   i3 - i1 plus the walk-first curriculum: --target-speed is the ramp's floor, so 1.0 -> 3.5 is the
#        first run in this project whose curriculum starts below a sprint.
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--entropy-coef","0.001","--track-var","8.0","--prog-w","0.25",
            "--fall-penalty","5.0","--init-std","0.25","--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","3.5","--action-scale","0.167","--run-name","i1_scale167")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","3.5","--action-scale","0.25","--run-name","i2_scale25")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","1.0","--target-speed-final","3.5","--speed-adaptive",
    "--action-scale","0.167","--run-name","i3_scale167_curr")) -NoNewWindow
Write-Host "batch I launched"
