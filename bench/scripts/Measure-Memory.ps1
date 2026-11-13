<#
.SYNOPSIS
    Samples system memory and target-app metrics to a CSV at a fixed interval. Read-only; no admin.

.DESCRIPTION
    Targets are resolved by executable name and/or PID, plus all descendant processes (Chromium / WebView2
    helpers), and re-resolved at every sample. Columns are listed in bench/README.md. Numbers are written with
    the invariant culture (dot decimal separator).

.EXAMPLE
    .\Measure-Memory.ps1 -TargetName WhatsApp.Root.exe -DurationSeconds 1800 -OutCsv ..\results\local-idle.csv

.EXAMPLE
    .\Measure-Memory.ps1 -TargetProcessId 1234 -IntervalSeconds 2 -DurationSeconds 10 -OutCsv test.csv
#>
[CmdletBinding()]
param(
    [string[]]$TargetName = @(),
    [int[]]$TargetProcessId = @(),
    [double]$IntervalSeconds = 5,
    [Parameter(Mandatory)][double]$DurationSeconds,
    [Parameter(Mandatory)][string]$OutCsv,
    [string]$Phase = 'manual',
    [string]$Arm = '',
    [string]$Run = '',
    [switch]$Append
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BenchCommon.ps1')

if ($TargetName.Count -eq 0 -and $TargetProcessId.Count -eq 0) {
    Write-Warning 'No target given: only system-wide columns will be meaningful.'
}

$cols = Get-SampleColumns
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($OutCsv))
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$writeHeader = -not ($Append -and (Test-Path $OutCsv))
$writer = New-Object IO.StreamWriter([IO.Path]::GetFullPath($OutCsv), [bool]$Append, (New-Object Text.UTF8Encoding($false)))
try {
    if ($writeHeader) { $writer.WriteLine(($cols -join ',')) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $next = 0.0
    $count = 0
    while ($sw.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $pids = Get-TargetProcessIds -Name $TargetName -ProcessId $TargetProcessId
        $row = Get-MetricSample -TargetPid $pids
        $row.elapsed_s = $sw.Elapsed.TotalSeconds
        $row.phase = $Phase; $row.arm = $Arm; $row.run = $Run
        $writer.WriteLine((ConvertTo-InvCsvLine -Row $row -Columns $cols))
        $writer.Flush()
        $count++
        $next += $IntervalSeconds
        $wait = $next - $sw.Elapsed.TotalSeconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
        elseif ($wait -lt -$IntervalSeconds) { $next = $sw.Elapsed.TotalSeconds }  # sampling slower than interval: do not burst
    }
    Write-Verbose "Wrote $count samples to $OutCsv"
}
finally {
    $writer.Dispose()
}
