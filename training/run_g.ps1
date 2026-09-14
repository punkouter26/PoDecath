# Batch G - can this body reach a walk at all, and does the double-support penalty forbid it?
#
# Control is f2_trackvar_fall (already run): track_var 8, prog_w 0.25, fall 5, alt-w 0.5, 3.5 m/s.
# Every run here is identical to it except the named knob.
#
#   g1_walk        - ask for 1.5 m/s (a walk) instead of 3.5 (a run).
#   g2_noalt       - keep 3.5 m/s, drop the double-support penalty.
#   g3_walk_noalt  - both.
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--entropy-coef","0.001","--track-var","8.0","--prog-w","0.25",
            "--fall-penalty","5.0","--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","1.5","--alt-w","0.5","--run-name","g1_walk")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","3.5","--alt-w","0.0","--run-name","g2_noalt")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","1.5","--alt-w","0.0","--run-name","g3_walk_noalt")) -NoNewWindow
Write-Host "batch G launched"
