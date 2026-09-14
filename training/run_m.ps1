# Batch M - stop paying the athlete for falling in the right direction.
#
# tools/gait_trace.py on k2, the fastest policy this project had produced: it lifts its left foot at
# 0.42 s, never puts it down, drifts at 0.68 m/s with the pelvis sinking from 0.90 m to 0.57 m, and
# lands at 1.18 s having taken zero footfalls. r_track and r_prog read centre-of-mass velocity and
# do not ask how it was produced, and toppling is the cheapest way for a standing body to acquire
# horizontal speed. Raising the fall penalty to 25 and then 50 (batch L) did not stop it, because the
# topple is being paid for the whole 1.5 s it lasts and the penalty arrives once at the end.
#
# --vel-gate fades both velocity terms out as the body drops or tilts, so the payment stops in the
# first tenth of a second of a fall rather than at the floor.
#
#   m1 - the gate, at the fall penalty batch L used.
#   m2 - the gate at the low fall penalty, to see whether the gate alone is enough.
#   m3 - the gate plus a 48-step rollout instead of 24, doubling the horizon the advantage estimate
#        sees. The other reading of "it topples" is that a 24-step (0.48 s) GAE window cannot see the
#        floor coming; this separates that from the reward shape.
$root = "C:\Users\punko\Downloads\PoDecath\training"
$launch = Join-Path $root "tools\launch_run.ps1"

$common = @("--task","target","--num-envs","4096","--iters","400",
            "--entropy-coef","0.001","--init-std","0.25","--action-scale","0.167",
            "--track-var","0.25","--prog-w","0.75","--posture-w","0.3",
            "--target-speed","1.0","--vel-gate","1.0",
            "--keep-old-runs","--no-tensorboard")

& $launch -RunName "m1_gate_fall25" @common --fall-penalty 25.0
& $launch -RunName "m2_gate_fall5"  @common --fall-penalty 5.0
& $launch -RunName "m3_gate_h48"    @common --fall-penalty 25.0 --steps 48
Write-Host "batch M launched"
