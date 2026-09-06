<#
.SYNOPSIS
    Builds PoDecath for Android, installs it on the attached phone, runs it, and proves it worked.

.DESCRIPTION
    The build was a menu item somebody had to remember to click with the right settings, and "does it
    work on the phone" was somebody looking at the phone. This does both, the same way, every time, and
    fails loudly rather than leaving a stale APK on the device.

    What it does, in order:

      1. Refuses to start if the Unity Editor has the project open. A batchmode build cannot take the
         project lock, and the failure it gives instead is a wall of Java stack trace that says nothing
         about the actual cause.
      2. Builds a release APK through PoDecath.EditorTools.AndroidBuild, which is where every mobile
         player setting lives. Scans the build log for compile errors, because Unity will happily log
         `error CS` and then exit 0.
      3. Installs it, launches it, and clears logcat first so what comes back is this run and not the
         last one.
      4. Watches it run for -RunSeconds, then presses HOME. Backgrounding is what makes the game write
         its telemetry export - see AgentTelemetry.OnApplicationPause - so this is how the summary gets
         written without anybody touching the screen.
      5. Pulls the telemetry off the device and prints the headline and the per-athlete verdicts, which
         is the actual output: what the agents did and what to change about it.
      6. Fails on any exception in the log, or on a missing telemetry export.

    Screenshots land in Build/Android/shots/. They are the check on the HUD frame that no log can make:
    title top-left, FPS top-centre, MENU top-right, DEBUG bottom-left, version bottom-right.

.EXAMPLE
    pwsh training/deploy_android.ps1
    pwsh training/deploy_android.ps1 -RunSeconds 90 -Development
    pwsh training/deploy_android.ps1 -SkipBuild          # reinstall and re-measure the APK already built
#>

[CmdletBinding()]
param(
    # Seconds to leave the game running before backgrounding it to collect telemetry. A 100 m takes
    # about 30 s of wall clock, so the default covers the menu, the load and one full race.
    [int]$RunSeconds = 60,

    # Skip the Unity build and deploy whatever APK is already at -ApkPath.
    [switch]$SkipBuild,

    # Skip regenerating the scenes. They are generated assets, so anything added to a scene builder is
    # not in any scene until the builders are re-run - which is how an APK that contains all the new
    # code and none of the new scene objects gets built. Skip this only when nothing under
    # Assets/Scripts/Editor has changed since the last deploy.
    [switch]$SkipSceneRebuild,

    # Development build: script debugging and the profiler can attach, and the Profiler counters the
    # FRAME page hides on a release build come back.
    [switch]$Development,

    # Which device, when more than one is attached.
    [string]$Serial = "",

    [string]$ApkPath = "Build/Android/PoDecath.apk",
    [string]$PackageId = "com.podecath.game"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$Apk = Join-Path $Root $ApkPath
$OutDir = Split-Path -Parent $Apk
$ShotDir = Join-Path $OutDir "shots"
$PullDir = Join-Path $OutDir "telemetry"
$BuildLog = Join-Path $OutDir "build.log"
$RunLog = Join-Path $OutDir "logcat.txt"

function Say($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m) { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }
function Die($m) { Write-Host "!!! $m" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $OutDir, $ShotDir, $PullDir | Out-Null

# ---------------------------------------------------------------- tools

function Find-Unity {
    $verFile = Join-Path $Root "ProjectSettings/ProjectVersion.txt"
    if (-not (Test-Path $verFile)) { Die "No ProjectSettings/ProjectVersion.txt - is $Root the project root?" }
    $version = ((Get-Content $verFile | Select-String '^m_EditorVersion:') -split ':')[1].Trim()

    $candidates = @(
        "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe",
        "$env:LOCALAPPDATA/Unity/Hub/Editor/$version/Editor/Unity.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return @{ Path = $c; Version = $version } } }
    Die "Unity $version not found. Looked in:`n      $($candidates -join "`n      ")"
}

function Find-Adb {
    $candidates = @(
        "$env:LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe",
        "$env:ANDROID_HOME/platform-tools/adb.exe",
        "C:/Program Files/Unity/Hub/Editor/*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
    )
    foreach ($c in $candidates) {
        $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    Die "adb not found. Install Android platform-tools, or set ANDROID_HOME."
}

# ---------------------------------------------------------------- build

# ---------------------------------------------------------------- running Unity

<#
    Runs one batchmode Unity pass and returns its real exit code.

    Start-Process -Wait -PassThru is not usable here. .NET can only read a process's exit code
    if it was holding an open handle when the process exited, and Start-Process does not keep
    one - so ExitCode comes back as -1 whether the run succeeded, failed, or is still going.
    That is not a hypothetical: it is what made this script announce "Scene rebuild exited -1"
    thirty seconds into a scene import that then ran to completion unattended.

    Touching $p.Handle caches the handle, which makes WaitForExit/ExitCode mean what they say.
#>
function Invoke-Unity($unity, $unityArgs, $label, $log) {
    $p = Start-Process -FilePath $unity.Path -ArgumentList $unityArgs -PassThru -NoNewWindow
    $null = $p.Handle
    $started = Get-Date

    # Unity in batchmode can be quiet for minutes at a time; a heartbeat off the log size is
    # the difference between "working" and "hung" from outside.
    while (-not $p.HasExited) {
        Start-Sleep -Seconds 15
        $size = if (Test-Path $log) { "{0:N0} KB" -f ((Get-Item $log).Length / 1KB) } else { "no log yet" }
        Write-Host ("    [{0:mm\:ss}] {1} - {2}" -f ((Get-Date) - $started), $label, $size) -ForegroundColor DarkGray
    }

    $p.WaitForExit()
    return $p.ExitCode
}

function Assert-EditorClosed {
    # The lockfile is the real answer; a running Unity.exe might be a different project.
    $lock = Join-Path $Root "Temp/UnityLockfile"
    if (-not (Test-Path $lock)) { return }
    try {
        $s = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
        $s.Close()
    } catch {
        Die "The Unity Editor has this project open, so a batchmode build cannot run.`n" +
            "    Close it (or run this with -SkipBuild to deploy the APK already at $ApkPath)."
    }
}

function Invoke-SceneRebuild($unity) {
    Assert-EditorClosed
    Say "Regenerating the scenes"
    Warn "This imports the White House mesh and re-solves the track; give it a few minutes."

    $log = Join-Path $OutDir "scenes.log"
    $unityArgs = @(
        "-quit", "-batchmode", "-nographics",
        "-buildTarget", "Android",
        "-projectPath", $Root,
        "-executeMethod", "PoDecath.EditorTools.AndroidBuild.RebuildScenesFromCommandLine",
        "-logFile", $log
    )
    $code = Invoke-Unity $unity $unityArgs "rebuilding scenes" $log
    if ($code -ne 0) {
        $tail = if (Test-Path $log) { Get-Content $log -Tail 40 } else { @("(no log)") }
        Write-Host ($tail -join "`n") -ForegroundColor DarkGray
        Die "Scene rebuild exited $code. Full log: $log"
    }
    Ok "scenes rebuilt (log: $log)"
}

function Invoke-Configure($unity) {
    Assert-EditorClosed
    Say "Applying the player settings"

    # Its own Unity invocation, and it has to stay that way. The API compatibility level, the scripting
    # backend and the stripping level all decide how the managed assemblies are compiled, so setting them
    # dirties every assembly in the project - and a run that sets them and then builds in the same session
    # builds the scenes against the editor's old assemblies. Unity's word for that is
    #
    #     Error building player because script class layout is incompatible between the editor and the player
    #
    # which is a real failure this project hit, after a nine-minute IL2CPP build, having changed nothing
    # but the API level. Exiting here and letting the build pass start up already compiled against the new
    # settings is what makes it impossible rather than intermittent.
    $log = Join-Path $OutDir "configure.log"
    $unityArgs = @(
        "-quit", "-batchmode", "-nographics",
        "-buildTarget", "Android",
        "-projectPath", $Root,
        "-executeMethod", "PoDecath.EditorTools.AndroidBuild.ConfigureFromCommandLine",
        "-logFile", $log
    )
    if ($Development) { $unityArgs += "-devBuild" }

    $code = Invoke-Unity $unity $unityArgs "applying settings" $log
    if ($code -ne 0) {
        $tail = if (Test-Path $log) { Get-Content $log -Tail 40 } else { @("(no log)") }
        Write-Host ($tail -join "`n") -ForegroundColor DarkGray
        Die "Applying the player settings exited $code. Full log: $log"
    }
    $applied = Select-String -Path $log -Pattern "Player settings applied: (.+)$" -ErrorAction SilentlyContinue
    if ($applied) { Ok $applied.Matches[0].Groups[1].Value.Trim() } else { Ok "settings applied (log: $log)" }
}

function Invoke-Build($unity) {
    Assert-EditorClosed
    Say "Building $(if ($Development) { 'development' } else { 'release' }) APK with Unity $($unity.Version)"
    Warn "This is an IL2CPP build; the first one on a machine takes several minutes."

    $unityArgs = @(
        "-quit", "-batchmode", "-nographics",
        "-buildTarget", "Android",
        "-projectPath", $Root,
        "-executeMethod", "PoDecath.EditorTools.AndroidBuild.BuildFromCommandLine",
        "-apkPath", $Apk,
        "-logFile", $BuildLog
    )
    if ($Development) { $unityArgs += "-devBuild" }

    $code = Invoke-Unity $unity $unityArgs "building APK" $BuildLog

    if (Test-Path $BuildLog) {
        # Unity logs compile errors and can still exit 0 when the failure was in an assembly the build
        # did not strictly need. It needed it.
        $compileErrors = Select-String -Path $BuildLog -ErrorAction SilentlyContinue `
            -Pattern "error CS\d+", "script class layout is incompatible", "Build Failed"
        if ($compileErrors) {
            Write-Host ""
            $compileErrors | Select-Object -First 25 | ForEach-Object { Write-Host "    $($_.Line.Trim())" -ForegroundColor Red }
            Die "$($compileErrors.Count) compile error(s). Full log: $BuildLog"
        }
    }

    # Named explicitly, because the tail of this failure is four hundred lines of memory-leak accounting
    # and the one line that says what happened is thousands of lines above it.
    if ((Test-Path $BuildLog) -and (Select-String -Path $BuildLog -Pattern "script class layout is incompatible" -Quiet)) {
        $extra = Select-String -Path $BuildLog -Pattern "has an extra field '(.+?)' of type '(.+?)'" -ErrorAction SilentlyContinue
        if ($extra) {
            Write-Host ""
            $extra | Select-Object -First 3 | ForEach-Object { Write-Host "    $($_.Line.Trim())" -ForegroundColor Red }
        }
        Die ("The editor and the player disagree about the layout of a serialized type.`n" +
             "    Despite the wording this is almost never stale assemblies - it is the API compatibility`n" +
             "    level. Setting it moves editorAssembliesCompatibilityLevel too, and under 4.8 the unsafe`n" +
             "    pointer fields in glTFast's Burst jobs are serialized in the player but not the editor.`n" +
             "    Check ConfigurePlayer in AndroidBuild.cs still leaves the level at NET_Standard.`n" +
             "    Full log: $BuildLog")
    }

    if ($code -ne 0) {
        $tail = if (Test-Path $BuildLog) { Get-Content $BuildLog -Tail 40 } else { @("(no log)") }
        Write-Host ($tail -join "`n") -ForegroundColor DarkGray
        Die "Unity exited $code. Full log: $BuildLog"
    }
    if (-not (Test-Path $Apk)) { Die "Unity reported success but $Apk is not there. Log: $BuildLog" }

    $mb = (Get-Item $Apk).Length / 1MB
    Ok ("APK built: {0} ({1:N1} MB)" -f $Apk, $mb)
}

# ---------------------------------------------------------------- device

function Resolve-Device($adb) {
    $lines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\sdevice$" }
    $serials = @($lines | ForEach-Object { ($_ -split "\s+")[0] })

    if ($serials.Count -eq 0) { Die "No Android device attached and authorised. Check the USB cable and the debugging prompt on the phone." }
    if ($Serial) {
        if ($serials -notcontains $Serial) { Die "Device '$Serial' is not attached. Attached: $($serials -join ', ')" }
        return $Serial
    }
    if ($serials.Count -gt 1) { Die "More than one device attached ($($serials -join ', ')). Pass -Serial." }
    return $serials[0]
}

function Shot($adb, $serial, $name) {
    $remote = "/sdcard/podecath-shot.png"
    # Via a file on the device rather than `exec-out ... > file`: PowerShell's redirection is text, and
    # a PNG through it comes out corrupt in a way that is not obvious until you try to open it.
    & $adb -s $serial shell screencap -p $remote | Out-Null
    $local = Join-Path $ShotDir "$name.png"
    & $adb -s $serial pull $remote $local 2>&1 | Out-Null
    & $adb -s $serial shell rm -f $remote | Out-Null
    if (Test-Path $local) { Ok "screenshot: $local" } else { Warn "screenshot '$name' failed" }
}

# ---------------------------------------------------------------- go

$unity = Find-Unity
$adb = Find-Adb
Ok "adb: $adb"

if (-not $SkipBuild) {
    # Settings first, in their own invocation, then scenes, then the build. See Invoke-Configure for why
    # the first of those three cannot be folded into the third.
    Invoke-Configure $unity
    if (-not $SkipSceneRebuild) { Invoke-SceneRebuild $unity }
    Invoke-Build $unity
}
elseif (-not (Test-Path $Apk)) { Die "-SkipBuild given but $Apk does not exist." }

$serial = Resolve-Device $adb
$model = (& $adb -s $serial shell getprop ro.product.model).Trim()
$release = (& $adb -s $serial shell getprop ro.build.version.release).Trim()
Say "Deploying to $model (Android $release, $serial)"

Say "Installing"
$install = & $adb -s $serial install -r -d $Apk 2>&1 | Out-String
if ($install -notmatch "Success") {
    Write-Host $install -ForegroundColor Red
    Die "Install failed."
}
$code = (& $adb -s $serial shell dumpsys package $PackageId | Select-String "versionCode=(\d+)").Matches[0].Groups[1].Value
$name = (& $adb -s $serial shell dumpsys package $PackageId | Select-String "versionName=(\S+)").Matches[0].Groups[1].Value
Ok "installed $PackageId $name (versionCode $code)"

# The telemetry directory is wiped so what gets pulled is unambiguously from this run.
$remoteFiles = "/sdcard/Android/data/$PackageId/files/telemetry"
& $adb -s $serial shell rm -rf $remoteFiles 2>&1 | Out-Null

Say "Launching"
& $adb -s $serial logcat -c 2>&1 | Out-Null
& $adb -s $serial shell monkey -p $PackageId -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null

Start-Sleep -Seconds 12
Shot $adb $serial "01-menu"

Say "Running for $RunSeconds s"
Start-Sleep -Seconds ([Math]::Max(1, $RunSeconds - 12))
Shot $adb $serial "02-running"

# The DEBUG sheet, opened by tapping the button the frame puts in the bottom-left corner. It is
# screenshotted because it is the one part of this that no log can check: the export proves the numbers
# were sampled, and only a picture proves they are legible on a phone and are not sitting under the
# gesture bar.
#
# The tap point is computed rather than hardcoded. The button is 180x80 reference pixels at the left of a
# 112-pixel row on the bottom edge, in a panel that is 1080 wide with match 0.5, so its centre in device
# pixels depends on the screen. Aiming above the button's centre keeps the tap inside it whether or not
# the device reports a bottom safe-area inset, which shifts the whole row up by the height of the gesture
# bar and is the difference between opening the sheet and pressing the home gesture.
Say "Opening the DEBUG sheet"
$wm = (& $adb -s $serial shell wm size) -replace '.*:\s*',''
$sw, $sh = $wm.Trim() -split 'x' | ForEach-Object { [int]$_ }
$scale = [Math]::Pow(2, (([Math]::Log($sw / 1080.0, 2)) + ([Math]::Log($sh / 1920.0, 2))) / 2)
$tapX = [int](106 * $scale)
$tapY = [int]($sh - (92 * $scale))
& $adb -s $serial shell input tap $tapX $tapY | Out-Null
Start-Sleep -Seconds 3
Shot $adb $serial "03-debug"
# Left open. The sheet sorts above the frame and covers the corner the DEBUG button is in, so the same tap
# would land on the sheet rather than close it - and it does not need closing, because backgrounding the
# app is what writes the export and that happens whichever screen is on.

# HOME rather than force-stop: onPause is what triggers the telemetry export, and a killed process
# writes nothing.
Say "Backgrounding to flush the telemetry export"
& $adb -s $serial shell input keyevent KEYCODE_HOME | Out-Null
Start-Sleep -Seconds 4

& $adb -s $serial logcat -d > $RunLog
Ok "log: $RunLog"

# ---------------------------------------------------------------- verify

$fail = @()

$exceptions = Select-String -Path $RunLog -Pattern "(AndroidRuntime: FATAL|Unhandled Exception|NullReferenceException|Exception: )" -ErrorAction SilentlyContinue
if ($exceptions) {
    Warn "$($exceptions.Count) exception line(s) in the log:"
    $exceptions | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.Line.Trim())" -ForegroundColor Red }
    $fail += "exceptions in the device log"
}

$missing = Select-String -Path $RunLog -Pattern "\[PoDecath\].*(missing|not built)|\[AppFrameView\]|no '.*' in " -ErrorAction SilentlyContinue
if ($missing) {
    Warn "UI wiring warnings - some screen did not find an element it expected:"
    $missing | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.Line.Trim())" -ForegroundColor Yellow }
    $fail += "UI elements missing at runtime"
}

Say "Pulling telemetry"
& $adb -s $serial pull "$remoteFiles" $PullDir 2>&1 | Out-Null
$latest = Get-ChildItem -Path $PullDir -Recurse -Filter "latest.json" -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $latest) {
    $fail += "no telemetry export on the device ($remoteFiles)"
    Warn "Nothing pulled. Either no agents ran, or the export failed - check $RunLog for [AgentTelemetry]."
} else {
    $t = Get-Content $latest.FullName -Raw | ConvertFrom-Json
    Ok "telemetry: $($latest.FullName)"

    Write-Host ""
    Write-Host "  ---------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "  $($t.scene)  ·  $($t.appVersion)  ·  $($t.device)" -ForegroundColor DarkGray
    Write-Host "  $($t.headline)" -ForegroundColor White
    Write-Host "  ---------------------------------------------------------------" -ForegroundColor DarkGray

    foreach ($a in $t.agents) {
        $colour = switch ($a.grade) { "Good" { "Green" } "Warn" { "Yellow" } "Bad" { "Red" } default { "Gray" } }
        Write-Host ""
        Write-Host "  $($a.name)" -ForegroundColor $colour -NoNewline
        Write-Host "  $($a.activeModel)" -ForegroundColor DarkGray
        Write-Host ("    speed {0:N2} m/s mean, {1:N2} peak   ·   {2:N1} m   ·   {3} fall(s)   ·   upright {4:N2}" -f `
            $a.speedMean, $a.speedPeak, $a.distance, $a.falls, $a.uprightMean) -ForegroundColor Gray
        if ($a.isRL) {
            Write-Host ("    obs clip {0:P1}   ·   clamped {1:P1}   ·   jitter {2:N2}   ·   {3:N0}/{4:N0} Hz   ·   {5:N2} ms" -f `
                $a.observationClipping, $a.targetClamping, $a.actionRate, $a.measuredHz, $a.configHz, $a.inferenceMs) -ForegroundColor Gray
        }
        Write-Host "    $($a.verdict)" -ForegroundColor $colour
    }
    Write-Host ""

    if ($t.agents.Count -eq 0) { $fail += "the export has no agents in it" }
}

Write-Host ""
if ($fail.Count -gt 0) {
    Die ("Deployed, but not clean:`n    - " + ($fail -join "`n    - "))
}
Say "Deployed and verified on $model."
Ok "screenshots: $ShotDir"
Ok "telemetry:   $PullDir"
