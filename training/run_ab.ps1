# Three-way A/B on the standing-still failure, all at identical settings except the knob under test.
#
# f0_control      - the configuration the 3-hour run used: tracking variance 2.0, fall penalty 20.
# f1_trackvar     - widen the tracking Gaussian so there is a gradient at low speed.
# f2_trackvar_fall- the same, plus a fall penalty low enough that trying is not ruinous.
#
# 4096 envs each rather than 8192 so all three fit in 12 GB at once and are graded against each
# other rather than against a differently-shaped run.
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--target-speed","3.5","--entropy-coef","0.001","--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","2.0","--prog-w","0.25","--fall-penalty","20.0","--run-name","f0_control")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","8.0","--prog-w","0.25","--fall-penalty","20.0","--run-name","f1_trackvar")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","8.0","--prog-w","0.25","--fall-penalty","5.0","--run-name","f2_trackvar_fall")) -NoNewWindow
Write-Host "three runs launched"
