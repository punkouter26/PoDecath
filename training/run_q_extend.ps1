# Batch Q extension: fill the owner's full 8-hour window. Waits for run_q.ps1 to finish, then keeps
# training the lap policy (resumed from q4_lap) until the deadline, leaving ~10 minutes for the gate.
param(
    [datetime]$Deadline = "2026-09-30 07:14:17",
    [int]$NumEnvs = 4096
)
$root = "C:\Users\punko\Downloads\PoDecath\training"
$py = Join-Path $root ".venv\Scripts\python.exe"
$logs = Join-Path $root "logs"
$status = Join-Path $logs "q_status.txt"
Set-Location $root

function Say([string]$msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg
    Add-Content -Path $status -Value $line
    Write-Host $line
}

while (-not (Select-String -Path $status -Pattern "batch Q finished|FAILED" -Quiet -ErrorAction SilentlyContinue)) {
    Start-Sleep 30
}
if (Select-String -Path $status -Pattern "FAILED" -Quiet) { Say "extension skipped: batch Q failed"; exit 1 }

$base = Join-Path $root "checkpoints\run_track\q4_lap\latest.pt"
$hours = (($Deadline - (Get-Date)).TotalHours) - 0.17
if ($hours -lt 0.1) { Say "extension skipped: no time left before $Deadline"; exit 0 }

# Start the speed curriculum just under where q4 left it, so the policy is not re-asked for a jog.
$ts = 2.5
try {
    $m = Get-Content (Join-Path $logs "scratch_policies\athlete_policy_config.json") -Raw | ConvertFrom-Json
    $ts = [Math]::Max(2.5, [double]$m.trained_by.target_speed - 0.5)
} catch {}

$run = "q5_lap_more"
Say ("start {0} (track) for {1:N2} h from q4_lap, target from {2:N2} m/s" -f $run, $hours, $ts)
& $py train_run.py --task track --num-envs $NumEnvs --iters 1000000 --save-every 25 --keep-old-runs `
    --no-tensorboard --run-name $run --resume $base `
    --track-var 0.25 --prog-w 0.75 --posture-w 0.3 --vel-gate 1.0 --spawn-facing 1.0 --action-scale 0.167 `
    --gait-w 1.0 --target-speed $ts --target-speed-final 5.0 --speed-adaptive --speed-step 0.05 `
    --fall-penalty 10.0 --entropy-coef 0.002 --max-hours $hours *> (Join-Path $logs "$run.log")
$ck = Join-Path $root "checkpoints\run_track\$run\latest.pt"
if (-not (Test-Path $ck)) { Say "FAILED $run - no checkpoint"; exit 1 }
Say "done $run -> $ck"

$gate = Join-Path $logs "q_gate.jsonl"
foreach ($v in @("3.6", "4.0")) {
    & $py eval_lap.py --ckpt $ck --episodes 10 --seconds 20 --num-envs 256 --target-speed $v `
        --json $gate --label "$run@$v" *> (Join-Path $logs "q5_gate_$v.log")
    Say "q5 gate at $v m/s: $((Get-Content (Join-Path $logs "q5_gate_$v.log") | Select-String -Pattern 'PASS|fail|speed' | Select-Object -Last 3) -join ' / ')"
}

# Leave the simulator's own window on the final policy (house rule 11).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*view_policy.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Process $py -ArgumentList @("view_policy.py", "--ckpt", $ck) -WorkingDirectory $root
Say "batch Q + extension finished (8 h window). Final policy: $ck ; ONNX in logs\scratch_policies (not published)"
