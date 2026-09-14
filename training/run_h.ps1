# Batch H - does a body that can hold its own stand pose actually learn to run?
#
# The model was regenerated on 2026-09-14 with a 3 degree forward lean at the ankle and 3x the leg
# and trunk joint stiffness (force limits unchanged, so torques stay human). Before that change the
# athlete toppled backwards under its own weight in 1.75 s with zero action and survived 0 of 40
# noisy resets to 5 s; after it, 92% hold for 10 s. Every run in this project until now was asking a
# policy to run with a body that was already falling over.
#
# Reward settings are f2_trackvar_fall's, the best measured before this, so the only new variable is
# the body -- except in h2 and h3, which each add one thing on top.
#
#   h1_phys      - the physics fix alone, against f2_trackvar_fall (old body, same reward).
#   h2_phys_curr - plus a speed curriculum that starts at a walk and ramps to a run as falls allow.
#                  --target-speed is the floor of that ramp, not the goal; every previous run set it
#                  to 3.5 and so could only ever ramp upward from a sprint.
#   h3_phys_init - plus resetting some episodes already moving, so the policy sees states at speed.
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--entropy-coef","0.001","--track-var","8.0","--prog-w","0.25",
            "--fall-penalty","5.0","--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","3.5","--run-name","h1_phys")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","1.0","--target-speed-final","3.5","--speed-adaptive",
    "--run-name","h2_phys_curr")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--target-speed","3.5","--init-speed","2.0","--run-name","h3_phys_init")) -NoNewWindow
Write-Host "batch H launched"
