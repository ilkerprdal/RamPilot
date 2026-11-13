<#
.SYNOPSIS
    Smoke test of the harness (about one minute). Touches only a dummy process it starts itself.

.DESCRIPTION
    1. Starts a dummy target (Start-DummyTarget.ps1).
    2. Samples it for a few seconds (Measure-Memory.ps1).
    3. Runs one EmptyWorkingSet pass against it and checks that its working set dropped.
    4. Runs a small memory hog (128 MB).
    5. Runs the orchestrator with the "none" and "cleaner" arms, 2 runs, seconds-long phases.
    6. Summarizes the results.
    Output goes to %TEMP%\RamPilotBench\selftest-<time>. The rampilot arm is not exercised here.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $env:TEMP ('RamPilotBench\selftest-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
$failures = 0
function Check([bool]$Cond, [string]$What) {
    if ($Cond) { Write-Host "PASS  $What" } else { Write-Host "FAIL  $What" -ForegroundColor Red; $script:failures++ }
}

$dummy = & (Join-Path $here 'Start-DummyTarget.ps1') -MB 200 -Seconds 240
try {
    Check (-not $dummy.HasExited) "dummy target started (pid $($dummy.Id), WS $([math]::Round($dummy.WorkingSet64 / 1MB)) MB)"

    $csv = Join-Path $out 'measure.csv'
    & (Join-Path $here 'Measure-Memory.ps1') -TargetProcessId $dummy.Id -IntervalSeconds 1 -DurationSeconds 4 -OutCsv $csv
    $rows = @(Import-Csv $csv)
    Check ($rows.Count -ge 2) "sampler wrote $($rows.Count) rows"
    Check ([double]::Parse($rows[-1].target_private_ws_mb, [cultureinfo]::InvariantCulture) -gt 150) "sampler sees the dummy's private WS ($($rows[-1].target_private_ws_mb) MB)"

    $dummy.Refresh(); $before = $dummy.WorkingSet64 / 1MB
    $r = & (Join-Path $here 'Invoke-EmptyWorkingSetLoop.ps1') -TargetProcessId $dummy.Id -Once -LogCsv (Join-Path $out 'cleaner.csv')
    $dummy.Refresh(); $after = $dummy.WorkingSet64 / 1MB
    Check ($r.Emptied -eq 1 -and $after -lt $before / 2) ("cleaner emptied the dummy: {0:N0} MB -> {1:N0} MB" -f $before, $after)

    $hog = & (Join-Path $here 'Start-MemoryHog.ps1') -TargetAvailableMB 1024 -MaxMB 128 -ChunkMB 32 -HoldSeconds 1 -OutCsv (Join-Path $out 'hog.csv')
    Check ($hog.AllocatedMB -gt 0) "hog allocated $($hog.AllocatedMB) MB in $($hog.AllocSeconds) s (retouch $($hog.RetouchMs) ms)"

    $res = Join-Path $out 'arms'
    & (Join-Path $here 'Invoke-BenchArms.ps1') -Arms none, cleaner -Runs 2 -Seed 42 -TargetProcessId $dummy.Id `
        -WarmupSeconds 2 -IdleSeconds 6 -PressureSeconds 2 -HogMaxMB 64 -CooldownSeconds 2 `
        -SampleIntervalSeconds 1 -CleanerIntervalSeconds 2 -OutDir $res
    $sched = @(Import-Csv (Join-Path $res 'schedule.csv'))
    Check (@($sched | Where-Object status -eq 'done').Count -eq 4) 'orchestrator completed 4 cells'

    $md = & (Join-Path $here 'Get-BenchSummary.ps1') -ResultsDir $res -BootstrapResamples 200
    Check ($md.Count -gt 2) 'summary produced a table'
    $md | Select-Object -First 8 | ForEach-Object { "      $_" }
}
finally {
    if (-not $dummy.HasExited) { Stop-Process -Id $dummy.Id -Force }
}

Write-Host "Output: $out"
if ($failures) { Write-Host "$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.'
