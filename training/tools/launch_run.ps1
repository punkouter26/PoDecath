# Launch one training run detached, with its console output going to logs/<run>.log rather than to
# the caller's terminal. Start-Process -NoNewWindow inherits the parent's stdout, so a batch of three
# runs writes three interleaved copies of Warp's kernel-load banner into whatever launched it.
param(
    [Parameter(Mandatory = $true)][string]$RunName,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$TrainArgs
)
$root = "C:\Users\punko\Downloads\PoDecath\training"
$py = Join-Path $root ".venv\Scripts\python.exe"
Start-Process $py `
    -ArgumentList (@((Join-Path $root "train_run.py")) + $TrainArgs + @("--run-name", $RunName)) `
    -WorkingDirectory $root `
    -RedirectStandardOutput (Join-Path $root "logs\$RunName.log") `
    -RedirectStandardError (Join-Path $root "logs\$RunName.err") `
    -NoNewWindow
Write-Host "launched $RunName"
