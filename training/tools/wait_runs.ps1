# Block until no train_run.py process remains. pgrep does not see Windows processes from the bash
# tool, so a bash wait loop returns immediately and grades a run that is still half finished.
param([int]$PollSeconds = 20)
while ($true) {
    $n = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
           Where-Object { $_.CommandLine -like '*train_run.py*' }).Count
    if ($n -eq 0) { break }
    Start-Sleep -Seconds $PollSeconds
}
Write-Host "all training runs finished"
