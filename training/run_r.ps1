# Batch R - overnight: make the lap runner faster, then steadier in a pack.
#
# Batch Q (2026-09-29, rl_optimization_log.md section 8) ended with q5 at 2.76 m/s and a diagnosis:
# the adaptive speed curriculum raised the target on fall rate alone, ran it to 5.5 m/s over an athlete
# doing 1.65, and then locked at its floor once full randomisation pushed the fall rate over the back-off
# line. Both halves are fixed in train_run.py (--speed-gap, and thresholds of 0.12 / 0.25 on a running
# average). The phone race of 2026-09-30 added the second problem: 3 of 8 down by 46 m, from each other.
#
# The .pt checkpoints of batch Q are not on this machine (they are not in git), so R starts from the
# shipped ONNX: tools/onnx_to_checkpoint.py rebuilds the actor, and --critic-warmup-iters fits a
# critic to it before the actor is allowed to move.
#
#   R1 speed   run_track from q5, adaptive target 2.5 -> 4.5 m/s, randomisation at 0.6 strength
#   R2 pack    run_pack (envs/pack.py) from R1's best: the same lap with figures to be bumped by
#   after each stage: tools/pick_best.py gates the last few checkpoints (lap, and lap-in-a-pack)
#              and keeps the best, because the newest checkpoint of a hunting curriculum is a coin toss
#   then       the MuJoCo viewer left open on the final policy (house rule 11)
#
# TensorBoard is started here on -TbPort (house rule 9). Nothing is published: exports go to
# logs/scratch_policies and logs/r_final, shipping is the owner's call.
param(
    [datetime]$Deadline = (Get-Date).Date.AddDays(1).AddHours(6.5),
    [int]$NumEnvs = 8192,
    [double]$SpeedShare = 0.58,          # fraction of the training time R1 gets; R2 gets the rest
    [int]$WaitMinutes = 60,              # how long to wait for another project's run to free the GPU
    [int]$TbPort = 6007,
    [string]$Warm = "checkpoints\warm\q5.pt",
    [string]$FromR1 = "",                # an R1 checkpoint: skip R1 and run the pack stage from it
    [double]$PackHours = 0               # with -FromR1, how long the pack stage runs
)
$root = $PSScriptRoot
$py = Join-Path $root ".venv\Scripts\python.exe"
$logs = Join-Path $root "logs"
$status = Join-Path $logs "r_status.txt"
$gate = Join-Path $logs "r_gate.jsonl"
Set-Location $root
New-Item -ItemType Directory -Force $logs | Out-Null
$script:viewer = $null

function Say([string]$msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg
    Add-Content -Path $status -Value $line
    Write-Host $line
}

# A laptop left alone goes to sleep, and a sleeping laptop trains nothing. Held for the life of this
# process and released when it exits.
Add-Type -Namespace PoDecath -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][PoDecath.Power]::SetThreadExecutionState([uint32]"0x80000001")   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

function OtherTraining {
    @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
        Where-Object { $_.CommandLine -match 'train(\.exe|_run\.py|\.py)' -and $_.CommandLine -notlike "*$root*" })
}

# One stage in the foreground, output to logs/<run>.log. Returns the directory its checkpoints are in.
function Stage([string]$Run, [string]$Task, [double]$Hours, [string[]]$TrainArgs) {
    Say ("start {0} ({1}) for {2:N2} h, {3} envs" -f $Run, $Task, $Hours, $script:envs)
    & $py train_run.py --task $Task --num-envs $script:envs --iters 10000000 --save-every 200 `
        --video-every-iters 2000 --keep-old-runs --no-tensorboard --run-name $Run `
        --max-hours $Hours @TrainArgs *> (Join-Path $logs "$Run.log")
    $dir = Join-Path $root ("checkpoints\{0}\{1}" -f $(if ($Task -eq "pack") { "run_pack" } else { "run_track" }), $Run)
    if (-not (Test-Path (Join-Path $dir "latest.pt"))) { Say "FAILED $Run - no checkpoint, see logs\$Run.log"; exit 1 }
    Say "done $Run ($(Summary $Run))"
    return $dir
}

# Last-20-iteration averages from the run's CSV.
function Summary([string]$Run) {
    $rows = @(Import-Csv (Join-Path $logs "$Run.csv") | Select-Object -Last 20)
    if ($rows.Count -eq 0) { return "no rows" }
    $avg = { param($k) ($rows | ForEach-Object { [double]$_.$k } | Measure-Object -Average).Average }
    "iter {0}, v {1:N2} m/s, fall {2:N2}, surv {3:N2}, duty {4:N2}" -f `
        $rows[-1].iter, (& $avg "v_toward"), (& $avg "fall_rate"), (& $avg "surv_ratio"), (& $avg "duty_factor")
}

# Gates checkpoints spread over the second half of a run, plus the ones named in $Also, keeps the
# winner as best.pt and returns the whole result (.best, .candidates). The earlier checkpoints are
# candidates so that a stage which made things worse hands on what it was given: the night cannot end
# below where it began.
#
# -Pack scores speed x staying up among the pack figures; without it the score is lap speed alone.
# R1 is picked on lap speed alone, and that matters: on 2026-10-01 its checkpoints ran 3.37 m/s but
# only 11-18 % of them stayed up in a pack (the faster runner hits harder), against 2.76 m/s and 30 %
# for the start, so the combined score would have handed R2 the slow runner and thrown the speed away.
# Teaching the fast one to stay up is what R2 is for; the combined score is for the end of the night.
function Pick([string]$Dir, [string]$Label, [string[]]$Also, [int]$Last = 8, [switch]$Pack) {
    $a = @("--run-dir", $Dir, "--last", "$Last", "--every", "0", "--json", $gate)
    foreach ($c in $Also) { $a += @("--ckpt", $c) }
    if ($Pack) { $a += "--pack" }
    & $py tools\pick_best.py @a *> (Join-Path $logs "$Label.pick.log")
    $best = Get-Content (Join-Path $logs "$Label.pick.log") | Select-String "^BEST" | Select-Object -Last 1
    Say "$Label $best"
    if (-not (Test-Path (Join-Path $Dir "best.pt"))) { Say "FAILED $Label - nothing could be gated"; exit 1 }
    return (Get-Content $gate | Select-Object -Last 1 | ConvertFrom-Json)
}

# House rule 11: the simulator's own window on the newest policy, replaced after every stage.
function Show([string]$Ck) {
    if ($script:viewer -and -not $script:viewer.HasExited) { Stop-Process -Id $script:viewer.Id -Force }
    $script:viewer = Start-Process $py -ArgumentList @("view_policy.py", "--ckpt", $Ck) -WorkingDirectory $root -PassThru
}

Say "batch R begins: deadline $Deadline, warm start $Warm"

# House rules 9 and 10. Nothing obsolete to clear on a fresh clone, but say so rather than assume.
$tb = Join-Path $logs "tb"
New-Item -ItemType Directory -Force $tb | Out-Null
Get-ChildItem $tb -Directory | Where-Object { $_.Name -notmatch '^r\d_' } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force; Say "tensorboard: removed obsolete run $($_.Name)" }
if (-not (Get-NetTCPConnection -LocalPort $TbPort -State Listen -ErrorAction SilentlyContinue)) {
    Start-Process $py -ArgumentList @("-m", "tensorboard.main", "--logdir", $tb, "--port", "$TbPort") -WindowStyle Hidden
}
Say "tensorboard: http://localhost:$TbPort"

# Another project's run on the same GPU halves both. Wait for it, up to a point, then share.
# -WaitMinutes 0 does not wait and does not halve: the run takes its turn on the GPU at full size.
$script:envs = $NumEnvs
$waited = 0
while ((OtherTraining).Count -gt 0 -and $waited -lt $WaitMinutes) { Start-Sleep 60; $waited++ }
if ($WaitMinutes -gt 0 -and (OtherTraining).Count -gt 0) {
    $script:envs = [int]($NumEnvs / 2)
    Say "another training run still holds the GPU after $waited min; sharing it at $($script:envs) envs"
} else { Say "GPU $(if ((OtherTraining).Count -gt 0) { 'shared with another run' } else { 'free' }) after $waited min of waiting" }

$common = @("--track-var", "0.25", "--prog-w", "0.75", "--posture-w", "0.3", "--vel-gate", "1.0",
            "--spawn-facing", "1.0", "--action-scale", "0.167", "--gait-w", "1.0",
            "--fall-penalty", "10.0", "--entropy-coef", "0.002", "--dr-strength", "0.6",
            "--target-speed-final", "4.5", "--speed-adaptive", "--speed-step", "0.01", "--speed-gap", "0.15",
            "--lr-adapt", "1.1", "--critic-warmup-iters", "100", "--no-value-clip")
# The last line of that is what six validation runs on 2026-09-30 bought (2048 envs, a few hundred
# iterations each, all resumed from the rebuilt q5):
#   step 0.05, gap 0.3, default rate controller      2.70 m/s, 0 falls -> 2.40 and 36 % falls by iteration 190
#   step 0.01, gap 0.15, --lr-adapt 1.1               held to 200, then 71 % falls by 290
#   the same reward q5 was trained under (target 5.0) 82 % falls by 400
#   actor frozen for the whole run (control)          0 falls, 2.70 m/s, to 350: the updates do the damage
#   as row 2 plus --no-value-clip                     2.61-2.74 m/s, falls at most 12 %, to 420
#   target 5.0 plus --no-value-clip                   dipped to 61 % falls at 380, back to 16 % by 420
# The clipped critic could not follow the policy it was judging (explained variance 0.93 -> 0.75 and
# staying there); unclipped it holds 0.85-0.91. See PPOConfig.value_clip.

# Where the warm start stands, on both gates, so the morning has a before to read the after against.
if ($FromR1 -and (Test-Path $gate)) {
    $before = (Get-Content $gate | Select-Object -First 1 | ConvertFrom-Json).best    # measured when the batch began
} else {
    & $py tools\pick_best.py --ckpt $Warm --pack --json $gate *> (Join-Path $logs "r0_before.pick.log")
    $before = (Get-Content $gate | Select-Object -Last 1 | ConvertFrom-Json).best
}
Say ("before: lap {0:N2} m/s, {1:P0} clean; in a pack {2:P0} clean" -f $before.speed, $before.clean, $before.pack_clean)

$d1 = Join-Path $root "checkpoints\run_track\r1_speed"
if ($FromR1) {
    # Continuing a batch whose R1 has already run: its chosen checkpoint goes straight to the pack stage.
    Copy-Item $FromR1 (Join-Path $d1 "best.pt") -Force
    $h2 = $PackHours
    Say "R1 already run; the pack stage starts from $FromR1"
} else {
    # 0.8 h is held back for the rounds of gating (two gates a candidate) and the exports.
    $train = ($Deadline - (Get-Date)).TotalHours - 0.8
    if ($train -lt 0.5) { Say "FAILED - only $([Math]::Round($train, 2)) h before $Deadline, not worth starting"; exit 1 }
    $h1 = $train * $SpeedShare
    $h2 = $train - $h1

    # R1. The critic is blank, so the first 100 iterations fit it and leave the actor alone.
    $d1 = Stage "r1_speed" "track" $h1 ($common + @("--resume", $Warm, "--target-speed", "2.5"))
    [void](Pick $d1 "r1_speed" @($Warm))
}
$r1best = Join-Path $d1 "best.pt"
Show $r1best

# R2. Starts the curriculum a little under the pace R1 was being asked for: being bumped costs pace
# before it is learned. The critic is warmed up again, because what a state is worth changes once
# there are figures to run into.
$v1 = [double](& $py -c "import torch; print(torch.load(r'$r1best', map_location='cpu', weights_only=False)['extra'].get('target_speed', 2.9))")
$ts2 = [Math]::Max(2.5, [Math]::Round($v1 - 0.4, 2))
$d2 = Stage "r2_pack" "pack" $h2 ($common + @("--resume", $r1best, "--target-speed", "$ts2",
                                               "--speed-fall-low", "0.20", "--speed-fall-high", "0.40"))

# The night's answer: speed x staying up in a pack, over R2's checkpoints, R1's best and the start.
$res = Pick $d2 "r2_pack" @($r1best, $Warm) -Last 6 -Pack
$b2 = $res.best
$b1 = $res.candidates | Where-Object { $_.ckpt -eq $r1best } | Select-Object -First 1
$final = Join-Path $d2 "best.pt"
$out = Join-Path $logs "r_final"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $final (Join-Path $out "best.pt") -Force
& $py export_checkpoint.py --ckpt $final --out (Join-Path $out "athlete_track.onnx") `
    --manifest (Join-Path $out "athlete_policy_config.json") *> (Join-Path $logs "r_export.log")
@{ before = $before; r1 = $b1; final = $b2; candidates = $res.candidates; envs = $script:envs; finished = (Get-Date).ToString("s") } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $logs "r_summary.json")
Show $final
Say ("batch R finished. before {0:N2} m/s / pack {1:P0}; R1 {2:N2} / {3:P0}; final {4:N2} / {5:P0} ({6}). ONNX in logs\r_final (not published)" -f `
    $before.speed, $before.pack_clean, $b1.speed, $b1.pack_clean, $b2.speed, $b2.pack_clean, (Split-Path $b2.ckpt -Leaf))
