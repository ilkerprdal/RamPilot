<#
.SYNOPSIS
    Memory-pressure generator: allocates and touches memory in chunks until available RAM falls to a target,
    holds it, then releases it. Records how long each chunk took.

.DESCRIPTION
    Used for the pressure scenario in bench/README.md. Every page of every chunk is written, so the memory is
    really resident (not just committed). Time per chunk rises when Windows has to trim, compress or page out
    other processes to make room: that is the cost the benchmark compares between arms.

    Guards: stops at -MaxMB even if the target is not reached; refuses a target below 512 MB available unless
    -Force; stops early if commit headroom drops below 1 GB. All memory is released on exit.

.EXAMPLE
    .\Start-MemoryHog.ps1 -TargetAvailableMB 1024 -MaxMB 12288 -HoldSeconds 60 -OutCsv ..\results\local-hog.csv
#>
[CmdletBinding()]
param(
    [double]$TargetAvailableMB = 1024,
    [int]$MaxMB = 4096,
    [int]$ChunkMB = 64,
    [double]$HoldSeconds = 30,
    [string]$OutCsv = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BenchCommon.ps1')

if ($TargetAvailableMB -lt 512 -and -not $Force) { throw 'TargetAvailableMB below 512 can make the system unresponsive; use -Force if you mean it.' }
if ($ChunkMB -lt 1 -or $ChunkMB -gt 1024) { throw 'ChunkMB must be between 1 and 1024.' }

if (-not ('RamPilotBench.Hog' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;

namespace RamPilotBench
{
    public static class Hog
    {
        static readonly List<byte[]> Chunks = new List<byte[]>();

        /// <summary>Allocates one chunk and writes one byte per 4 KiB page so it becomes resident.</summary>
        public static void AddChunk(int mb)
        {
            byte[] b = new byte[(long)mb * 1048576];
            for (int i = 0; i < b.Length; i += 4096) b[i] = 1;
            Chunks.Add(b);
        }

        /// <summary>Touches every page again; returns elapsed milliseconds (refault cost of the hog itself).</summary>
        public static double TouchAll()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (byte[] b in Chunks) for (int i = 0; i < b.Length; i += 4096) b[i]++;
            return sw.Elapsed.TotalMilliseconds;
        }

        public static int Count { get { return Chunks.Count; } }

        public static void Release()
        {
            Chunks.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
'@
}

$log = $null
if ($OutCsv) {
    $full = [IO.Path]::GetFullPath($OutCsv)
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $log = New-Object IO.StreamWriter($full, $false, (New-Object Text.UTF8Encoding($false)))
    $log.WriteLine('timestamp_utc,event,allocated_mb,chunk_ms,avail_mb')
}
function Write-HogLog([string]$Event, [double]$AllocatedMb, $Ms) {
    if ($log) {
        $log.WriteLine(((Format-Inv ([datetime]::UtcNow)), $Event, (Format-Inv $AllocatedMb), (Format-Inv $Ms),
                (Format-Inv ([RamPilotBench.Native]::AvailableMb()))) -join ',')
        $log.Flush()
    }
}

$allocated = 0
$chunkTimes = New-Object System.Collections.Generic.List[double]
$total = [Diagnostics.Stopwatch]::StartNew()
try {
    Write-HogLog 'start' 0 $null
    while ($allocated + $ChunkMB -le $MaxMB -and [RamPilotBench.Native]::AvailableMb() -gt $TargetAvailableMB) {
        $os = Get-CimInstance Win32_OperatingSystem -Property FreeVirtualMemory
        if ([double]$os.FreeVirtualMemory / 1KB -lt 1024 + $ChunkMB) { Write-Warning 'Commit headroom below 1 GB; stopping.'; break }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        [RamPilotBench.Hog]::AddChunk($ChunkMB)
        $ms = $sw.Elapsed.TotalMilliseconds
        $allocated += $ChunkMB
        $chunkTimes.Add($ms)
        Write-HogLog 'chunk' $allocated $ms
    }
    $allocSeconds = $total.Elapsed.TotalSeconds
    Write-HogLog 'reached' $allocated $null

    $hold = [Diagnostics.Stopwatch]::StartNew()
    while ($hold.Elapsed.TotalSeconds -lt $HoldSeconds) {
        Start-Sleep -Milliseconds ([int][math]::Min(1000, [math]::Max(1, ($HoldSeconds - $hold.Elapsed.TotalSeconds) * 1000)))
    }
    $retouchMs = [RamPilotBench.Hog]::TouchAll()
    Write-HogLog 'retouch' $allocated $retouchMs

    $sorted = @($chunkTimes | Sort-Object)
    [pscustomobject]@{
        AllocatedMB      = $allocated
        AllocSeconds     = [math]::Round($allocSeconds, 2)
        ChunkMsMedian    = if ($sorted.Count) { [math]::Round($sorted[[int][math]::Floor(($sorted.Count - 1) / 2)], 1) } else { $null }
        ChunkMsMax       = if ($sorted.Count) { [math]::Round($sorted[-1], 1) } else { $null }
        RetouchMs        = [math]::Round($retouchMs, 1)
        AvailableMBAtEnd = [math]::Round([RamPilotBench.Native]::AvailableMb())
    }
}
finally {
    [RamPilotBench.Hog]::Release()
    Write-HogLog 'released' 0 $null
    if ($log) { $log.Dispose() }
}
