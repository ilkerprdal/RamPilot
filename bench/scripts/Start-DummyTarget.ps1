<#
.SYNOPSIS
    Starts a dummy target process for testing the harness: it allocates and touches -MB of memory, then sleeps.

.DESCRIPTION
    The dummy is a tiny console program compiled on first use to %TEMP%\RamPilotBench\rp-bench-dummy.exe.
    Use it to check the sampler, the EmptyWorkingSet baseline and the orchestrator without touching real apps.
    Returns the started System.Diagnostics.Process.

.EXAMPLE
    $d = .\Start-DummyTarget.ps1 -MB 200 -Seconds 120
    .\Invoke-EmptyWorkingSetLoop.ps1 -TargetProcessId $d.Id -Once
    Stop-Process -Id $d.Id
#>
[CmdletBinding()]
param(
    [int]$MB = 200,
    [int]$Seconds = 120,
    [int]$HotMB = 16   # memory the dummy keeps touching every second (a small hot set)
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP 'RamPilotBench'
$exe = Join-Path $dir 'rp-bench-dummy.exe'
if (-not (Test-Path $exe)) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    Add-Type -OutputType ConsoleApplication -OutputAssembly $exe -Language CSharp -TypeDefinition @'
using System;
using System.Threading;

public static class Program
{
    public static int Main(string[] args)
    {
        int mb = args.Length > 0 ? int.Parse(args[0]) : 200;
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 120;
        int hotMb = args.Length > 2 ? int.Parse(args[2]) : 16;
        byte[] cold = new byte[(long)mb * 1048576];
        for (int i = 0; i < cold.Length; i += 4096) cold[i] = 1;
        byte[] hot = new byte[(long)hotMb * 1048576];
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            for (int i = 0; i < hot.Length; i += 4096) hot[i]++;
            Thread.Sleep(1000);
        }
        GC.KeepAlive(cold);
        return 0;
    }
}
'@
}

$p = Start-Process -FilePath $exe -ArgumentList $MB, $Seconds, $HotMB -WindowStyle Hidden -PassThru
# wait until the cold allocation is resident
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    Start-Sleep -Milliseconds 200
    $p.Refresh()
} while (-not $p.HasExited -and $p.WorkingSet64 -lt ($MB * 0.9 * 1MB) -and [DateTime]::UtcNow -lt $deadline)
$p
