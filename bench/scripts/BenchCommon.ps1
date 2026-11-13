# Shared helpers for the RamPilot benchmark harness. Dot-source this file; it defines functions only.
# Compatible with Windows PowerShell 5.1 and PowerShell 7. Needs no administrator rights.
# Sampling uses CIM performance classes and GlobalMemoryStatusEx, not Get-Counter (whose counter names are
# localized and which is slow to start).

Set-StrictMode -Version 2.0

$script:Inv = [System.Globalization.CultureInfo]::InvariantCulture

if (-not ('RamPilotBench.Native' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace RamPilotBench
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        /// <summary>Available physical memory in MB (free + zeroed + standby), as Task Manager reports it.</summary>
        public static double AvailableMb()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception();
            return m.ullAvailPhys / 1048576.0;
        }

        public static double TotalMb()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception();
            return m.ullTotalPhys / 1048576.0;
        }
    }
}
'@
}

function Format-Inv {
    <# Formats a value for CSV with the invariant culture (dot decimal separator) regardless of OS locale. #>
    param($Value, [int]$Decimals = 2)
    if ($null -eq $Value) { return '' }
    if ($Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) {
        return ([math]::Round([double]$Value, $Decimals)).ToString($script:Inv)
    }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o', $script:Inv) }
    $s = [string]::Format($script:Inv, '{0}', $Value)
    if ($s.Contains(',') -or $s.Contains('"')) { return '"' + $s.Replace('"', '""') + '"' }
    return $s
}

function ConvertTo-InvCsvLine {
    param([System.Collections.IDictionary]$Row, [string[]]$Columns)
    ($Columns | ForEach-Object { Format-Inv $Row[$_] }) -join ','
}

function Get-NormalizedExeName {
    param([string]$Name)
    if ($Name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { return $Name.Substring(0, $Name.Length - 4) }
    return $Name
}

function Get-TargetProcessIds {
    <#
    Returns the PIDs of the target app trees: every process whose executable name matches -Name, or whose
    PID is in -ProcessId, plus all of their descendants (Chromium / WebView2 helpers). A child is only
    accepted if it started after its parent, which filters out stale parent PIDs that were reused.
    #>
    param([string[]]$Name = @(), [int[]]$ProcessId = @())

    $procs = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, Name, CreationDate)
    $byPid = @{}
    $children = @{}
    foreach ($p in $procs) {
        $byPid[[int]$p.ProcessId] = $p
        $pp = [int]$p.ParentProcessId
        if (-not $children.ContainsKey($pp)) { $children[$pp] = New-Object System.Collections.Generic.List[int] }
        $children[$pp].Add([int]$p.ProcessId)
    }

    $wanted = @{}
    foreach ($n in $Name) { $wanted[(Get-NormalizedExeName $n).ToLowerInvariant()] = $true }

    $roots = New-Object System.Collections.Generic.List[int]
    foreach ($p in $procs) {
        $exe = (Get-NormalizedExeName ([string]$p.Name)).ToLowerInvariant()
        if ($wanted.ContainsKey($exe) -or ($ProcessId -contains [int]$p.ProcessId)) { $roots.Add([int]$p.ProcessId) }
    }

    $result = New-Object 'System.Collections.Generic.HashSet[int]'
    $queue = New-Object System.Collections.Generic.Queue[int]
    foreach ($r in $roots) { if ($result.Add($r)) { $queue.Enqueue($r) } }
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        if (-not $children.ContainsKey($parent)) { continue }
        $parentStart = $byPid[$parent].CreationDate
        foreach ($c in $children[$parent]) {
            if ($c -eq $parent) { continue }
            $childStart = $byPid[$c].CreationDate
            if ($null -ne $parentStart -and $null -ne $childStart -and $childStart -lt $parentStart) { continue }
            if ($result.Add($c)) { $queue.Enqueue($c) }
        }
    }
    return , ([int[]]@($result))
}

function Get-ProcessCpuSeconds {
    param([string]$Name)
    $p = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($p.Count -eq 0) { return $null }
    $sum = 0.0
    foreach ($x in $p) { try { $sum += $x.TotalProcessorTime.TotalSeconds } catch { } }
    return $sum
}

$script:SampleColumns = @(
    'timestamp_utc', 'elapsed_s', 'phase', 'arm', 'run',
    'avail_mb', 'committed_mb', 'commit_limit_mb', 'standby_mb', 'modified_mb',
    'page_reads_s', 'page_writes_s', 'pages_input_s', 'pages_output_s',
    'compression_ws_mb', 'compression_cpu_s',
    'target_procs', 'target_ws_mb', 'target_private_ws_mb', 'target_faults_s', 'target_cpu_pct_core',
    'engine_private_ws_mb', 'engine_cpu_s'
)

function Get-SampleColumns { return $script:SampleColumns }

function Get-MetricSample {
    <#
    One sample of system and target metrics. Rates (_s) are the per-second values of the CIM formatted
    performance classes; *_cpu_s are cumulative CPU seconds. Missing values are empty in the CSV.
    #>
    param([int[]]$TargetPid = @(), [string]$EngineName = 'RamPilot')

    $row = [ordered]@{}
    $row.timestamp_utc = [datetime]::UtcNow

    $m = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Memory -Property `
        AvailableMBytes, CommittedBytes, CommitLimit, ModifiedPageListBytes, StandbyCacheCoreBytes,
        StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, PageReadsPersec, PageWritesPersec,
        PagesInputPersec, PagesOutputPersec
    $row.avail_mb = [RamPilotBench.Native]::AvailableMb()
    $row.committed_mb = [double]$m.CommittedBytes / 1MB
    $row.commit_limit_mb = [double]$m.CommitLimit / 1MB
    $row.standby_mb = ([double]$m.StandbyCacheCoreBytes + [double]$m.StandbyCacheNormalPriorityBytes + [double]$m.StandbyCacheReserveBytes) / 1MB
    $row.modified_mb = [double]$m.ModifiedPageListBytes / 1MB
    $row.page_reads_s = [double]$m.PageReadsPersec
    $row.page_writes_s = [double]$m.PageWritesPersec
    $row.pages_input_s = [double]$m.PagesInputPersec
    $row.pages_output_s = [double]$m.PagesOutputPersec

    $perf = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Property `
            Name, IDProcess, WorkingSet, WorkingSetPrivate, PageFaultsPersec, PercentProcessorTime)

    $mc = $perf | Where-Object { $_.Name -eq 'Memory Compression' } | Select-Object -First 1
    $row.compression_ws_mb = if ($mc) { [double]$mc.WorkingSet / 1MB } else { $null }
    $row.compression_cpu_s = Get-ProcessCpuSeconds -Name 'Memory Compression'

    $set = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($t in $TargetPid) { [void]$set.Add($t) }
    $n = 0; $ws = 0.0; $pws = 0.0; $pf = 0.0; $cpu = 0.0
    $engineWs = $null
    foreach ($p in $perf) {
        $id = [int]$p.IDProcess
        if ($id -ne 0 -and $set.Contains($id)) {
            $n++
            $ws += [double]$p.WorkingSet
            $pws += [double]$p.WorkingSetPrivate
            $pf += [double]$p.PageFaultsPersec
            $cpu += [double]$p.PercentProcessorTime
        }
        if ($p.Name -eq $EngineName) { $engineWs = [double]$p.WorkingSetPrivate / 1MB }
    }
    $row.target_procs = $n
    $row.target_ws_mb = $ws / 1MB
    $row.target_private_ws_mb = $pws / 1MB
    $row.target_faults_s = $pf
    $row.target_cpu_pct_core = $cpu
    $row.engine_private_ws_mb = $engineWs
    $row.engine_cpu_s = Get-ProcessCpuSeconds -Name $EngineName
    return $row
}

function Get-MachineInfo {
    <# Machine description for the run manifest. No user or host names are recorded. #>
    $os = Get-CimInstance Win32_OperatingSystem
    $cs = Get-CimInstance Win32_ComputerSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue)
    $mm = $null
    try { $mm = Get-MMAgent -ErrorAction Stop } catch { }
    [ordered]@{
        os_caption              = $os.Caption
        os_build                = $os.BuildNumber
        os_version              = $os.Version
        ram_total_mb            = [math]::Round([double]$cs.TotalPhysicalMemory / 1MB)
        cpu                     = ($cpu.Name -replace '\s+', ' ').Trim()
        cpu_logical             = $cpu.NumberOfLogicalProcessors
        pagefile_allocated_mb   = ($pf | Measure-Object -Property AllocatedBaseSize -Sum).Sum
        pagefile_auto           = [bool]$cs.AutomaticManagedPagefile
        memory_compression      = if ($mm) { [bool]$mm.MemoryCompression } else { $null }
        page_combining          = if ($mm) { [bool]$mm.PageCombining } else { $null }
        powershell              = $PSVersionTable.PSVersion.ToString()
    }
}
