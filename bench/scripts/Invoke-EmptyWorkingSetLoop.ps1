<#
.SYNOPSIS
    Baseline "RAM cleaner" for the benchmark: calls EmptyWorkingSet on the target app trees every N seconds.

.DESCRIPTION
    This reproduces what typical RAM cleaners do, so RamPilot can be compared against it. It is a benchmark
    arm, not a tool to use day to day: it evicts hot and cold pages alike.

    Safety rails: an explicit target is required; targets named like system, security, shell or harness
    processes are refused outright (with their child trees); such processes are also skipped if they appear
    inside a target tree; a tree larger than -MaxProcesses aborts the pass; this script's own process and its
    parent are never touched; only processes the current user can open are touched.
    No administrator rights are needed or used.

.EXAMPLE
    .\Invoke-EmptyWorkingSetLoop.ps1 -TargetName WhatsApp.Root.exe -IntervalSeconds 300 -DurationSeconds 1800

.EXAMPLE
    # Single pass against a dummy process, with a log of before/after working sets
    .\Invoke-EmptyWorkingSetLoop.ps1 -TargetProcessId 1234 -Once -LogCsv cleaner.csv
#>
[CmdletBinding()]
param(
    [string[]]$TargetName = @(),
    [int[]]$TargetProcessId = @(),
    [double]$IntervalSeconds = 300,
    [double]$DurationSeconds = 0,   # 0 = until stopped
    [switch]$Once,
    [string]$LogCsv = '',
    [int]$MaxProcesses = 40   # an app tree larger than this usually means the wrong target (e.g. a shell)
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BenchCommon.ps1')

if ($TargetName.Count -eq 0 -and $TargetProcessId.Count -eq 0) {
    throw 'Give -TargetName and/or -TargetProcessId. This script never acts on all processes.'
}

$refused = @(
    'system', 'idle', 'registry', 'memory compression', 'smss', 'csrss', 'wininit', 'winlogon', 'services', 'lsass',
    'svchost', 'dwm', 'explorer', 'fontdrvhost', 'audiodg', 'msmpeng', 'nissrv', 'securityhealthservice',
    'powershell', 'pwsh', 'conhost', 'windowsterminal', 'rampilot', 'rampilot.panel'
)

foreach ($n in $TargetName) {
    if ($refused -contains (Get-NormalizedExeName $n).ToLowerInvariant()) {
        throw "Refusing target '$n': system, security, shell and harness processes (and their child trees) are never trimmed."
    }
}

if (-not ('RamPilotBench.Cleaner' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace RamPilotBench
{
    public static class Cleaner
    {
        const uint PROCESS_SET_QUOTA = 0x0100;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr h);

        /// <summary>Returns 0 on success, otherwise the Win32 error code.</summary>
        public static int Empty(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try { return EmptyWorkingSet(h) ? 0 : Marshal.GetLastWin32Error(); }
            finally { CloseHandle(h); }
        }
    }
}
'@
}

$self = $PID
$parent = [int](Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -Property ParentProcessId).ParentProcessId

$log = $null
if ($LogCsv) {
    $full = [IO.Path]::GetFullPath($LogCsv)
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $exists = Test-Path $full
    $log = New-Object IO.StreamWriter($full, $true, (New-Object Text.UTF8Encoding($false)))
    if (-not $exists) { $log.WriteLine('timestamp_utc,pass,pid,name,ws_before_mb,ws_after_mb,result') }
}

function Invoke-Pass([int]$Pass) {
    $pids = Get-TargetProcessIds -Name $TargetName -ProcessId $TargetProcessId
    if ($pids.Count -gt $MaxProcesses) {
        throw "Targets resolve to $($pids.Count) processes (limit $MaxProcesses). A target is probably a shell or launcher; pick the app itself, or raise -MaxProcesses."
    }
    $freed = 0.0; $ok = 0; $skipped = 0
    foreach ($id in $pids) {
        if ($id -eq $self -or $id -eq $parent -or $id -le 4) { $skipped++; continue }
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($refused -contains $p.ProcessName.ToLowerInvariant()) { $skipped++; continue }
        $before = $p.WorkingSet64 / 1MB
        $rc = [RamPilotBench.Cleaner]::Empty($id)
        $p.Refresh()
        $after = $p.WorkingSet64 / 1MB
        if ($rc -eq 0) { $ok++; $freed += [math]::Max(0, $before - $after) }
        if ($log) {
            $log.WriteLine(((Format-Inv ([datetime]::UtcNow)), $Pass, $id, (Format-Inv $p.ProcessName),
                    (Format-Inv $before), (Format-Inv $after), $rc) -join ',')
            $log.Flush()
        }
    }
    Write-Verbose ("pass {0}: {1} processes emptied, {2} skipped, working set -{3:N1} MB" -f $Pass, $ok, $skipped, $freed)
    [pscustomobject]@{ Pass = $Pass; Emptied = $ok; Skipped = $skipped; WorkingSetDropMb = [math]::Round($freed, 1) }
}

try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pass = 0
    do {
        $pass++
        Invoke-Pass $pass
        if ($Once) { break }
        $next = $pass * $IntervalSeconds
        while ($sw.Elapsed.TotalSeconds -lt $next) {
            if ($DurationSeconds -gt 0 -and $sw.Elapsed.TotalSeconds -ge $DurationSeconds) { break }
            Start-Sleep -Milliseconds 500
        }
    } while ($DurationSeconds -le 0 -or $sw.Elapsed.TotalSeconds -lt $DurationSeconds)
}
finally {
    if ($log) { $log.Dispose() }
}
