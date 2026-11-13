# RamPilot Phase 0 - proof-of-concept experiment
# Applies a fixed hard working-set maximum to one process, measures it, then restores the ORIGINAL limits.
param(
    [string]$ProcessName = "WhatsApp.Root",
    [double]$Ratio = 0.4,          # cap = current working set * Ratio
    [int]$CapSeconds = 60,         # how long the cap is held (s)
    [int]$AfterSeconds = 30,       # monitoring after the cap is removed (restore / refault) (s)
    [int]$Interval = 5             # sampling interval (s)
)
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class WS {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
}
'@

$PROCESS_SET_QUOTA = 0x0100; $PROCESS_QUERY_INFORMATION = 0x0400
$MIN_DISABLE = 0x2; $MAX_ENABLE = 0x4

$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $p) { throw "Process not found: $ProcessName" }

$outDir = Join-Path $PSScriptRoot "results"; New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir ("phase0_{0}_{1}.csv" -f $ProcessName, (Get-Date -Format "yyyyMMdd_HHmmss"))

function Measure-Target([string]$phase) {
    $p.Refresh()
    $mem = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $mc = Get-Process -Name "Memory Compression" -ErrorAction SilentlyContinue
    $pf = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").PageFaults
    [pscustomobject]@{
        Time = (Get-Date -Format "HH:mm:ss"); Phase = $phase
        WS_MB = [math]::Round($p.WorkingSet64/1MB,1)
        Private_MB = [math]::Round($p.PrivateMemorySize64/1MB,1)
        PageFaults = $pf
        Available_MB = $mem.AvailableMBytes      # free + standby (trimmed clean pages show up here)
        Compression_MB = if ($mc) { [math]::Round($mc.WorkingSet64/1MB,1) } else { $null }
    }
}

$h = [WS]::OpenProcess($PROCESS_SET_QUOTA -bor $PROCESS_QUERY_INFORMATION, $false, $p.Id)
if ($h -eq [IntPtr]::Zero) { throw "OpenProcess failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

$oMin = [UIntPtr]::Zero; $oMax = [UIntPtr]::Zero; $oFlags = 0
[void][WS]::GetProcessWorkingSetSizeEx($h, [ref]$oMin, [ref]$oMax, [ref]$oFlags)
"Target: $($p.ProcessName) (PID $($p.Id)) | original min=$($oMin.ToUInt64()/1MB) MB max=$($oMax.ToUInt64()/1MB) MB flags=$oFlags"

$rows = @()
$rows += Measure-Target "before"
$capBytes = [uint64]([math]::Max(16MB, $p.WorkingSet64 * $Ratio))
$minBytes = [uint64](1MB)
"Cap: {0:N0} MB" -f ($capBytes/1MB)

try {
    if (-not [WS]::SetProcessWorkingSetSizeEx($h, [UIntPtr]$minBytes, [UIntPtr]$capBytes, ($MIN_DISABLE -bor $MAX_ENABLE))) {
        throw "SetProcessWorkingSetSizeEx failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    }
    for ($t = 0; $t -lt $CapSeconds; $t += $Interval) { Start-Sleep -Seconds $Interval; $rows += Measure-Target "capped" }
}
finally {
    # Explicitly disable the hard limits (0x2 MIN_DISABLE | 0x8 MAX_DISABLE) and put back the original min/max sizes.
    # CAUTION (Phase 0 finding): restoring a small original max (~1.3 MB) made the kernel trim the working set
    # to that size even though the limit is soft (256 MB -> 5 MB). So the max is set above the current WS;
    # a soft max is ignored anyway while memory is plentiful.
    $p.Refresh()
    $safeMax = [uint64]([math]::Max($oMax.ToUInt64(), [uint64]$p.WorkingSet64 * 2))
    $ok = [WS]::SetProcessWorkingSetSizeEx($h, $oMin, [UIntPtr]$safeMax, (0x2 -bor 0x8))
    $nMin=[UIntPtr]::Zero; $nMax=[UIntPtr]::Zero; $nFlags=0
    [void][WS]::GetProcessWorkingSetSizeEx($h, [ref]$nMin, [ref]$nMax, [ref]$nFlags)
    "Restore: $ok | min=$($nMin.ToUInt64()/1MB) MB max=$($nMax.ToUInt64()/1MB) MB flags=$nFlags"
}
for ($t = 0; $t -lt $AfterSeconds; $t += $Interval) { Start-Sleep -Seconds $Interval; $rows += Measure-Target "after" }
[void][WS]::CloseHandle($h)

$rows | Export-Csv -NoTypeInformation -Encoding UTF8 $csv
$rows | Format-Table -AutoSize
"CSV: $csv"
