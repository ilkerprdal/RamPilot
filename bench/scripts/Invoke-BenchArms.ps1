<#
.SYNOPSIS
    Runs the three-arm benchmark (none / rampilot / cleaner) in randomized order and records metrics to CSV.

.DESCRIPTION
    For every (run, arm) cell:
      warmup   no tool active; apps settle             (WarmupSeconds)
      idle     arm active; apps left alone in the background  (IdleSeconds)
      pressure arm active; Start-MemoryHog.ps1 drives available RAM down  (PressureSeconds, 0 = skip)
      cooldown arm stopped; recovery observed          (CooldownSeconds)
    Metrics are sampled throughout to <OutDir>\samples\run<NN>-<arm>.csv, tagged with the phase.

    Arm order is shuffled independently within each run (randomized complete block design) with a recorded
    seed. Progress is kept in <OutDir>\schedule.csv, so a benchmark can be continued with -Resume, for
    example one cell per boot with -MaxCells 1 (see bench/README.md, "Independence").

    Focus-latency measurement is not automated yet; see bench/README.md.

    The script never caps or trims anything itself. The rampilot arm runs RamPilot.exe; the cleaner arm runs
    Invoke-EmptyWorkingSetLoop.ps1 against the same targets. Needs no administrator rights.

.EXAMPLE
    # Full benchmark, defaults from bench/README.md
    .\Invoke-BenchArms.ps1 -TargetName WhatsApp.Root.exe, Discord.exe -RamPilotPath C:\Tools\RamPilot\RamPilot.exe

.EXAMPLE
    # Short smoke test against a dummy process, no RamPilot
    $d = .\Start-DummyTarget.ps1 -MB 200 -Seconds 300
    .\Invoke-BenchArms.ps1 -Arms none, cleaner -Runs 2 -TargetProcessId $d.Id -WarmupSeconds 2 -IdleSeconds 6 `
        -PressureSeconds 3 -HogMaxMB 128 -CooldownSeconds 2 -SampleIntervalSeconds 1 -CleanerIntervalSeconds 3
#>
[CmdletBinding()]
param(
    [ValidateSet('none', 'rampilot', 'cleaner')][string[]]$Arms = @('none', 'rampilot', 'cleaner'),
    [ValidateRange(1, 1000)][int]$Runs = 10,
    [int]$Seed = -1,
    [string[]]$TargetName = @(),
    [int[]]$TargetProcessId = @(),
    [double]$WarmupSeconds = 600,
    [double]$IdleSeconds = 1800,
    [double]$PressureSeconds = 300,
    [double]$CooldownSeconds = 120,
    [double]$SampleIntervalSeconds = 5,
    [string]$RamPilotPath = '',
    [double]$CleanerIntervalSeconds = 300,
    [double]$HogTargetAvailableMB = 1024,
    [int]$HogMaxMB = 0,   # 0 = total RAM; the available-memory target normally stops the hog first
    [string]$OutDir = '',
    [switch]$Resume,
    [int]$MaxCells = 0    # 0 = all remaining cells
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BenchCommon.ps1')

if ($TargetName.Count -eq 0 -and $TargetProcessId.Count -eq 0) { throw 'Give -TargetName and/or -TargetProcessId.' }
if ($Arms -contains 'rampilot') {
    if (-not $RamPilotPath -or -not (Test-Path $RamPilotPath)) { throw 'The rampilot arm needs -RamPilotPath pointing to RamPilot.exe.' }
    $RamPilotPath = (Resolve-Path $RamPilotPath).Path
}
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) ('results\local-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force (Join-Path $OutDir 'samples'), (Join-Path $OutDir 'logs') | Out-Null

$hostExe = (Get-Process -Id $PID).Path
$schedulePath = Join-Path $OutDir 'schedule.csv'
$manifestPath = Join-Path $OutDir 'manifest.json'

# ---------- schedule ----------
if ($Resume -and (Test-Path $schedulePath)) {
    $schedule = @(Import-Csv $schedulePath)
    Write-Host "Resuming: $(@($schedule | Where-Object status -ne 'done').Count) of $($schedule.Count) cells left."
}
else {
    if ($Seed -lt 0) { $Seed = Get-Random -Minimum 0 -Maximum 2147483647 }
    $rng = New-Object System.Random($Seed)
    $cellList = New-Object System.Collections.Generic.List[object]
    $cell = 0
    for ($r = 1; $r -le $Runs; $r++) {
        $order = @($Arms | Sort-Object { $rng.Next() })
        foreach ($a in $order) {
            $cell++
            $cellList.Add([pscustomobject]@{ cell = $cell; run = $r; arm = $a; status = 'pending'; started_utc = ''; ended_utc = '' })
        }
    }
    $schedule = $cellList.ToArray()
    $schedule | Export-Csv -NoTypeInformation -Encoding UTF8 $schedulePath

    $targetsNow = Get-TargetProcessIds -Name $TargetName -ProcessId $TargetProcessId
    $apps = @(foreach ($id in $targetsNow) {
            $p = Get-Process -Id $id -ErrorAction SilentlyContinue
            if ($p) { [ordered]@{ exe = $p.ProcessName; version = $(try { $p.MainModule.FileVersionInfo.ProductVersion } catch { $null }) } }
        }) | Sort-Object { $_.exe } -Unique
    $manifest = [ordered]@{
        created_utc       = (Get-Date).ToUniversalTime().ToString('o')
        seed              = $Seed
        arms              = $Arms
        runs              = $Runs
        target_names      = $TargetName
        target_pids_given = $TargetProcessId.Count
        apps              = $apps
        rampilot_version  = if ($RamPilotPath) { (Get-Item $RamPilotPath).VersionInfo.ProductVersion } else { $null }
        phases_s          = [ordered]@{ warmup = $WarmupSeconds; idle = $IdleSeconds; pressure = $PressureSeconds; cooldown = $CooldownSeconds }
        sample_interval_s = $SampleIntervalSeconds
        cleaner_interval_s = $CleanerIntervalSeconds
        hog               = [ordered]@{ target_available_mb = $HogTargetAvailableMB; max_mb = $HogMaxMB }
        machine           = Get-MachineInfo
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $manifestPath
    Write-Host "Seed $Seed; $($schedule.Count) cells; output in $OutDir"
}

# ---------- helpers ----------
function Save-Schedule { $schedule | Export-Csv -NoTypeInformation -Encoding UTF8 $schedulePath }

function Quote([string]$s) { '"' + $s.Replace('"', '\"') + '"' }

function Send-EngineCommand([string]$Json) {
    # Protocol v2: per-session pipe, one JSON object per line. Returns the reply line or $null.
    $name = 'RamPilot.' + (Get-Process -Id $PID).SessionId
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $c.Connect(2000)
        $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true
        $rd = New-Object IO.StreamReader($c)
        $w.WriteLine('{"cmd":"hello","protocol":2}'); [void]$rd.ReadLine()
        $w.WriteLine($Json); return $rd.ReadLine()
    }
    catch { return $null }
    finally { $c.Dispose() }
}

function Invoke-Phase {
    param([string]$Phase, [double]$Seconds, [IO.StreamWriter]$Writer, [Diagnostics.Stopwatch]$Clock,
        [string]$Arm, [int]$Run, [Diagnostics.Process]$Until)
    $cols = Get-SampleColumns
    $phaseClock = [Diagnostics.Stopwatch]::StartNew()
    $next = 0.0
    while ($phaseClock.Elapsed.TotalSeconds -lt $Seconds) {
        $pids = Get-TargetProcessIds -Name $TargetName -ProcessId $TargetProcessId
        $row = Get-MetricSample -TargetPid $pids
        $row.elapsed_s = $Clock.Elapsed.TotalSeconds
        $row.phase = $Phase; $row.arm = $Arm; $row.run = $Run
        $Writer.WriteLine((ConvertTo-InvCsvLine -Row $row -Columns $cols)); $Writer.Flush()
        if ($Until -and $Until.HasExited) { break }
        $next += $SampleIntervalSeconds
        $wait = $next - $phaseClock.Elapsed.TotalSeconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int][math]::Min($wait * 1000, [math]::Max(0, ($Seconds - $phaseClock.Elapsed.TotalSeconds) * 1000))) }
        elseif ($wait -lt -$SampleIntervalSeconds) { $next = $phaseClock.Elapsed.TotalSeconds }
    }
}

function Start-Arm([string]$Arm, [int]$Run) {
    $activeSeconds = $IdleSeconds + $PressureSeconds + 60
    switch ($Arm) {
        'none' { return $null }
        'rampilot' {
            $minutes = [math]::Ceiling($activeSeconds / 60)
            return Start-Process -FilePath $RamPilotPath -ArgumentList '--minutes', $minutes -PassThru
        }
        'cleaner' {
            $script = Join-Path $PSScriptRoot 'Invoke-EmptyWorkingSetLoop.ps1'
            $log = Join-Path $OutDir ('logs\run{0:D2}-cleaner.csv' -f $Run)
            $targets = @()
            if ($TargetName.Count) { $targets += '-TargetName ' + (($TargetName | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ',') }
            if ($TargetProcessId.Count) { $targets += '-TargetProcessId ' + ($TargetProcessId -join ',') }
            $cmd = "& '{0}' {1} -IntervalSeconds {2} -DurationSeconds {3} -LogCsv '{4}'" -f $script.Replace("'", "''"), ($targets -join ' '),
                $CleanerIntervalSeconds.ToString([cultureinfo]::InvariantCulture), $activeSeconds.ToString([cultureinfo]::InvariantCulture), $log.Replace("'", "''")
            return Start-Process -FilePath $hostExe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', (Quote $cmd) -WindowStyle Hidden -PassThru
        }
    }
}

function Stop-Arm([string]$Arm, $Proc) {
    if ($Arm -eq 'rampilot') {
        if ($Proc -and -not $Proc.HasExited) {
            [void](Send-EngineCommand '{"cmd":"quit"}')
            if (-not $Proc.WaitForExit(30000)) {
                Write-Warning 'RamPilot did not exit after quit; terminating it (the guardian clears limits).'
                Stop-Process -Id $Proc.Id -Force
            }
        }
        $rec = Start-Process -FilePath $RamPilotPath -ArgumentList '--recover' -PassThru -WindowStyle Hidden
        [void]$rec.WaitForExit(30000)
    }
    elseif ($Proc -and -not $Proc.HasExited) {
        Stop-Process -Id $Proc.Id -Force
    }
}

# ---------- run ----------
$done = 0
foreach ($c in $schedule) {
    if ($c.status -eq 'done') { continue }
    if ($MaxCells -gt 0 -and $done -ge $MaxCells) { break }
    if (@(Get-Process -Name RamPilot -ErrorAction SilentlyContinue).Count -gt 0) { throw 'RamPilot is already running. Exit it before benchmarking, so every arm starts clean.' }
    $initial = Get-TargetProcessIds -Name $TargetName -ProcessId $TargetProcessId
    if ($initial.Count -eq 0) { throw 'No target process is running.' }

    $run = [int]$c.run; $arm = [string]$c.arm
    Write-Host ("[{0}] cell {1}/{2}: run {3}, arm {4}" -f (Get-Date -Format 'HH:mm:ss'), $c.cell, $schedule.Count, $run, $arm)
    $c.status = 'running'; $c.started_utc = (Get-Date).ToUniversalTime().ToString('o'); Save-Schedule

    $csv = Join-Path $OutDir ('samples\run{0:D2}-{1}.csv' -f $run, $arm)
    $w = New-Object IO.StreamWriter($csv, $false, (New-Object Text.UTF8Encoding($false)))
    $armProc = $null
    try {
        $w.WriteLine(((Get-SampleColumns) -join ','))
        $clock = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Phase -Phase 'warmup' -Seconds $WarmupSeconds -Writer $w -Clock $clock -Arm $arm -Run $run

        $armProc = Start-Arm $arm $run
        if ($armProc) {
            Start-Sleep -Seconds 2
            if ($armProc.HasExited) { throw "The $arm arm exited right after starting (exit code $($armProc.ExitCode)). Run it by hand to see the error." }
        }
        Invoke-Phase -Phase 'idle' -Seconds $IdleSeconds -Writer $w -Clock $clock -Arm $arm -Run $run

        if ($PressureSeconds -gt 0) {
            $hogMax = if ($HogMaxMB -gt 0) { $HogMaxMB } else { [int][RamPilotBench.Native]::TotalMb() }
            $hogCsv = Join-Path $OutDir ('logs\run{0:D2}-{1}-hog.csv' -f $run, $arm)
            $hog = Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', (Quote (Join-Path $PSScriptRoot 'Start-MemoryHog.ps1')),
                '-TargetAvailableMB', $HogTargetAvailableMB.ToString([cultureinfo]::InvariantCulture), '-MaxMB', $hogMax,
                '-HoldSeconds', $PressureSeconds.ToString([cultureinfo]::InvariantCulture), '-OutCsv', (Quote $hogCsv)
            # allocation time is not bounded by PressureSeconds; allow up to 10 minutes extra
            Invoke-Phase -Phase 'pressure' -Seconds ($PressureSeconds + 600) -Writer $w -Clock $clock -Arm $arm -Run $run -Until $hog
            if (-not $hog.HasExited) { Stop-Process -Id $hog.Id -Force }
        }

        Stop-Arm $arm $armProc; $armProc = $null
        Invoke-Phase -Phase 'cooldown' -Seconds $CooldownSeconds -Writer $w -Clock $clock -Arm $arm -Run $run
        $c.status = 'done'
    }
    catch {
        $c.status = 'failed'
        throw
    }
    finally {
        if ($armProc) { Stop-Arm $arm $armProc }
        $w.Dispose()
        $c.ended_utc = (Get-Date).ToUniversalTime().ToString('o'); Save-Schedule
    }
    $done++
}

$left = @($schedule | Where-Object status -ne 'done').Count
Write-Host "Finished $done cell(s); $left left. Summarize with: .\Get-BenchSummary.ps1 -ResultsDir '$OutDir'"
