# Batch Q - overnight: learn to run fast from scratch, then take it to the lap.
#
# The 2026-09-15 optimisation loop ended on a plateau: every lap policy fine-tuned from lap_v80 tops
# out near 3.4 m/s, and 4,000 more iterations of the same recipe made it worse. Its recommendation
# (rl_optimization_log.md 2.8) was a policy that learns speed from scratch on an adaptive curriculum
# rather than inheriting a slow one. The checkpoints from that lineage are also gone from this machine,
# so every stage below starts from nothing and hands its latest.pt to the next.
#
#   Q1 walk     batch O's recipe: cheap falls, real exploration, gait clock -> first steps
#   Q2 firm     batch P's reversal: falls cost again, exploration down -> keeps the stride
#   Q3 speed    run_to_target, adaptive target 1.0 -> 5.5 m/s, raised only while falls stay rare
#   Q4 lap      run_track resumed from Q3, adaptive target 2.5 -> 5.0 m/s on the rooftop loop
#   then        deterministic lap gate (eval_lap.py) at 3.6 and 4.0 m/s, and the MuJoCo viewer left
#               open on the final policy
#
# gait_period stays 0.8 s because Unity's PolicyConfig carries one period for every policy. Nothing
# is published: exports go to logs/scratch_policies, shipping is the owner's call.
param(
    [double]$Q1Hours = 0.3,
    [double]$Q2Hours = 0.6,
    [double]$Q3Hours = 4.0,
    [double]$Q4Hours = 2.5,
    [int]$NumEnvs = 4096
)
$root = "C:\Users\punko\Downloads\PoDecath\training"
$py = Join-Path $root ".venv\Scripts\python.exe"
$logs = Join-Path $root "logs"
$status = Join-Path $logs "q_status.txt"
Set-Location $root
New-Item -ItemType Directory -Force $logs | Out-Null
$script:viewer = $null

function Say([string]$msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg
    Add-Content -Path $status -Value $line
    Write-Host $line
}

# One stage in the foreground, output to logs/<run>.log. Returns the checkpoint it finished with.
function Stage([string]$Run, [string]$Task, [string[]]$TrainArgs) {
    Say "start $Run ($Task)"
    & $py train_run.py --task $Task --num-envs $NumEnvs --iters 1000000 --save-every 25 `
        --keep-old-runs --no-tensorboard --run-name $Run @TrainArgs *> (Join-Path $logs "$Run.log")
    $dir = if ($Task -eq "track") { "run_track" } else { "run_to_target" }
    $ck = Join-Path $root "checkpoints\$dir\$Run\latest.pt"
    if (-not (Test-Path $ck)) { Say "FAILED $Run - no checkpoint, see logs\$Run.log"; exit 1 }
    Say "done $Run -> $ck ($(Summary $Run))"
    Show $ck
    return $ck
}

# Last-20-iteration averages from the run's CSV, the numbers that say walking vs statue vs falling.
function Summary([string]$Run) {
    $rows = @(Import-Csv (Join-Path $logs "$Run.csv") | Select-Object -Last 20)
    if ($rows.Count -eq 0) { return "no rows" }
    $avg = { param($k) ($rows | ForEach-Object { [double]$_.$k } | Measure-Object -Average).Average }
    "iter {0}, v {1:N2} m/s, fall {2:N2}, surv {3:N2}, duty {4:N2}" -f `
        $rows[-1].iter, (& $avg "v_toward"), (& $avg "fall_rate"), (& $avg "surv_ratio"), (& $avg "duty_factor")
}

function Walking([string]$Run) {
    $rows = @(Import-Csv (Join-Path $logs "$Run.csv") | Select-Object -Last 20)
    $v = ($rows | ForEach-Object { [double]$_.v_toward } | Measure-Object -Average).Average
    $d = ($rows | ForEach-Object { [double]$_.duty_factor } | Measure-Object -Average).Average
    return ($v -gt 0.25 -and $d -lt 0.9)
}

# House rule 11: the simulator's own window on the newest policy, replaced after every stage.
function Show([string]$Ck) {
    if ($script:viewer -and -not $script:viewer.HasExited) { Stop-Process -Id $script:viewer.Id -Force }
    $script:viewer = Start-Process $py -ArgumentList @("view_policy.py", "--ckpt", $Ck) -WorkingDirectory $root -PassThru
}

$common = @("--track-var", "0.25", "--prog-w", "0.75", "--posture-w", "0.3", "--vel-gate", "1.0",
            "--spawn-facing", "1.0", "--action-scale", "0.167", "--gait-w", "1.0")

Say "batch Q begins: Q1 $Q1Hours h, Q2 $Q2Hours h, Q3 $Q3Hours h, Q4 $Q4Hours h, $NumEnvs envs"

# Q1. A seed that settles into a statue is retried once; the rest of the night is built on this stage.
$q1 = "q1_walk"
$ck = Stage $q1 "target" ($common + @("--target-speed", "1.0", "--init-std", "0.5", "--entropy-coef", "0.01",
                                      "--fall-penalty", "2.0", "--max-hours", "$Q1Hours"))
if (-not (Walking $q1)) {
    Say "$q1 is not stepping; retrying with seed 1"
    $q1 = "q1_walk_seed1"
    $ck = Stage $q1 "target" ($common + @("--target-speed", "1.0", "--init-std", "0.5", "--entropy-coef", "0.01",
                                          "--fall-penalty", "2.0", "--max-hours", "$Q1Hours", "--seed", "1"))
}

# Q2.
$ck = Stage "q2_firm" "target" ($common + @("--resume", $ck, "--target-speed", "1.0", "--fall-penalty", "10.0",
                                            "--entropy-coef", "0.003", "--max-hours", "$Q2Hours"))

# Q3. The adaptive curriculum only moves on iterations where episodes ended, and backs off when the
# fall rate passes 15 %, so it searches for the fastest pace this body can hold rather than assuming one.
# Double-support penalty stays on: a run has none.
$ck = Stage "q3_speed" "target" ($common + @("--resume", $ck, "--target-speed", "1.0", "--target-speed-final", "5.5",
                                             "--speed-adaptive", "--speed-step", "0.05", "--fall-penalty", "10.0",
                                             "--entropy-coef", "0.002", "--max-hours", "$Q3Hours"))

# Q4. The lap: carrot on the centre line, off-deck terminates. Starts the curriculum at 2.5 m/s so the
# policy is not asked to relearn a jog it already has.
$ck = Stage "q4_lap" "track" ($common + @("--resume", $ck, "--target-speed", "2.5", "--target-speed-final", "5.0",
                                          "--speed-adaptive", "--speed-step", "0.05", "--fall-penalty", "10.0",
                                          "--entropy-coef", "0.002", "--max-hours", "$Q4Hours"))

# The gate the 2026-09-15 loop could not pass (3.60 m/s, 90 % clean on every episode), plus 4.0.
$gate = Join-Path $logs "q_gate.jsonl"
foreach ($v in @("3.6", "4.0")) {
    & $py eval_lap.py --ckpt $ck --episodes 10 --seconds 20 --num-envs 256 --target-speed $v `
        --json $gate --label "q4_lap@$v" *> (Join-Path $logs "q_gate_$v.log")
    Say "gate at $v m/s: $((Get-Content (Join-Path $logs "q_gate_$v.log") | Select-String -Pattern 'PASS|fail|speed' | Select-Object -Last 3) -join ' / ')"
}
Say "batch Q finished. Final policy: $ck ; ONNX in logs\scratch_policies (not published)"
