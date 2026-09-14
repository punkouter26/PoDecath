# Batch K - narrow the tracking kernel so standing still stops being paid for it.
#
# Batch J cut the posture income (alive + upright + heading) from 0.789 to 0.237 per step and the
# athlete stayed a statue. The per-term accounting says why: r_track simply took over as the free
# income. It is a Gaussian of variance track_var about the commanded speed, so at a 1.0 m/s command
# a body standing perfectly still scores
#
#     track_var 8.0 -> 1.5 * exp(-1/8)   = 1.33 of 1.5   (measured rt_track 1.328)
#     track_var 2.0 -> 1.5 * exp(-1/2)   = 0.91 of 1.5   (measured rt_track 0.939)
#     track_var 0.5 -> 1.5 * exp(-1/0.5) = 0.20 of 1.5
#     track_var 0.25-> 1.5 * exp(-1/0.25)= 0.03 of 1.5
#
# The kernel was widened (2.0 -> 8.0) to give the reward a gradient at zero speed against a 3.5 m/s
# target, which it does. Against a 1.0 m/s target the same width pays 89% of the tracking reward for
# not moving. The gradient at low speed should come from r_prog, which is linear in speed and has the
# same slope everywhere, so these narrow the kernel and raise the linear term to carry it.
#
# Target speed is fixed at 1.0 rather than adaptive: the curriculum never ramps at these fall rates
# (it is gated on fall_rate < 0.05), and a target the policy cannot observe changing underneath it is
# a non-stationary reward for no benefit.
#
#   k1 - track_var 0.5,  prog_w 0.75
#   k2 - track_var 0.25, prog_w 0.75   (narrower still)
#   k3 - track_var 0.5,  prog_w 1.5    (same kernel, twice the linear pull)
$py = "C:\Users\punko\Downloads\PoDecath\training\.venv\Scripts\python.exe"
Set-Location "C:\Users\punko\Downloads\PoDecath\training"

$common = @("--task","target","--num-envs","4096","--iters","220",
            "--entropy-coef","0.001","--fall-penalty","5.0",
            "--init-std","0.25","--action-scale","0.167",
            "--target-speed","1.0","--posture-w","0.3",
            "--keep-old-runs","--no-tensorboard")

Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","0.5","--prog-w","0.75","--run-name","k1_var05")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","0.25","--prog-w","0.75","--run-name","k2_var025")) -NoNewWindow
Start-Process $py -ArgumentList (@("train_run.py") + $common + @(
    "--track-var","0.5","--prog-w","1.5","--run-name","k3_var05_prog15")) -NoNewWindow
Write-Host "batch K launched"
