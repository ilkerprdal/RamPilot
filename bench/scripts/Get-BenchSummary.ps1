<#
.SYNOPSIS
    Summarizes a results folder from Invoke-BenchArms.ps1: per-cell metrics, then per-arm medians, p95 and
    bootstrap 95% confidence intervals of the median.

.DESCRIPTION
    Per cell (one run of one arm):
      private_ws_mb      median target private working set, steady idle (last half of the idle phase)
      avail_mb           median available memory, steady idle
      store_mb           median Memory Compression working set, steady idle
      page_writes_s      mean system page writes/s, whole idle phase
      store_cpu_s        Memory Compression CPU seconds used during idle + pressure
      engine_cpu_s       RamPilot CPU seconds used during idle + pressure (empty for other arms)
      hog_alloc_s        seconds the hog needed to reach its target (pressure phase)
      hog_chunk_p95_ms   p95 time per hog chunk
      hog_retouch_ms     time for the hog to touch all its memory again after the hold
      pressure_reads_s   mean system page reads/s during pressure (hard-fault I/O)

    Then, per arm: n, median, bootstrap 95% CI of the median (percentile method) and p95; and for each arm
    other than "none", the paired per-run difference against "none" with its bootstrap CI.

    Writes summary-cells.csv and summary.md into the results folder and prints the Markdown tables.

.EXAMPLE
    .\Get-BenchSummary.ps1 -ResultsDir ..\results\local-20261113-101500
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDir,
    [int]$BootstrapResamples = 2000,
    [int]$BootstrapSeed = 1
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BenchCommon.ps1')
$inv = [cultureinfo]::InvariantCulture

function ConvertTo-Num($s) {
    if ($null -eq $s -or $s -eq '') { return $null }
    return [double]::Parse($s, $inv)
}

function Get-Quantile([double[]]$x, [double]$q) {
    # linear interpolation between closest ranks (type 7, as in R and numpy defaults)
    if (-not $x -or $x.Count -eq 0) { return $null }
    $s = [double[]]$x.Clone()
    [Array]::Sort($s)
    $h = ($s.Count - 1) * $q
    $lo = [math]::Floor($h); $hi = [math]::Ceiling($h)
    return $s[$lo] + ($h - $lo) * ($s[$hi] - $s[$lo])
}

function Get-Median([double[]]$x) { Get-Quantile $x 0.5 }

function Get-BootstrapCi([double[]]$x, [int]$B, [System.Random]$Rng) {
    if (-not $x -or $x.Count -lt 2) { return @($null, $null) }
    $meds = New-Object double[] $B
    $n = $x.Count
    $buf = New-Object double[] $n
    # note: PowerShell variable names are case-insensitive, so the loop index must not be called $b
    for ($k = 0; $k -lt $B; $k++) {
        for ($i = 0; $i -lt $n; $i++) { $buf[$i] = $x[$Rng.Next($n)] }
        $meds[$k] = Get-Median $buf
    }
    return @((Get-Quantile $meds 0.025), (Get-Quantile $meds 0.975))
}

function Get-Col($rows, [string]$name) {
    # leading comma: return the array as one object so 0- and 1-element results are not unrolled
    , [double[]]@($rows | ForEach-Object { ConvertTo-Num $_.$name } | Where-Object { $null -ne $_ })
}

function Get-Mean([double[]]$x) {
    if (-not $x -or $x.Count -eq 0) { return $null }
    return ($x | Measure-Object -Average).Average
}

function Get-Delta($rows, [string]$name) {
    $v = Get-Col $rows $name
    if ($v.Count -lt 2) { return $null }
    return $v[-1] - $v[0]
}

$ResultsDir = (Resolve-Path $ResultsDir).Path
$files = @(Get-ChildItem (Join-Path $ResultsDir 'samples') -Filter 'run*.csv')
if ($files.Count -eq 0) { throw "No sample files in $ResultsDir\samples." }

$cells = foreach ($f in $files) {
    if ($f.BaseName -notmatch '^run(\d+)-(\w+)$') { continue }
    $run = [int]$Matches[1]; $arm = $Matches[2]
    $rows = @(Import-Csv $f.FullName)
    $idle = @($rows | Where-Object phase -eq 'idle')
    $press = @($rows | Where-Object phase -eq 'pressure')
    $steady = if ($idle.Count -ge 2) { $idle[[int][math]::Floor($idle.Count / 2)..($idle.Count - 1)] } else { $idle }
    $active = @($idle + $press)

    $hogFile = Join-Path $ResultsDir ('logs\run{0:D2}-{1}-hog.csv' -f $run, $arm)
    $hogAlloc = $null; $hogP95 = $null; $hogRetouch = $null
    if (Test-Path $hogFile) {
        $h = @(Import-Csv $hogFile)
        $t0 = $h | Where-Object event -eq 'start' | Select-Object -First 1
        $t1 = $h | Where-Object event -eq 'reached' | Select-Object -First 1
        if ($t0 -and $t1) {
            $hogAlloc = ([datetime]::Parse($t1.timestamp_utc, $inv, 'RoundtripKind') - [datetime]::Parse($t0.timestamp_utc, $inv, 'RoundtripKind')).TotalSeconds
        }
        $hogP95 = Get-Quantile (Get-Col @($h | Where-Object event -eq 'chunk') 'chunk_ms') 0.95
        $rt = $h | Where-Object event -eq 'retouch' | Select-Object -First 1
        if ($rt) { $hogRetouch = ConvertTo-Num $rt.chunk_ms }
    }

    [pscustomobject][ordered]@{
        run              = $run
        arm              = $arm
        private_ws_mb    = Get-Median (Get-Col $steady 'target_private_ws_mb')
        avail_mb         = Get-Median (Get-Col $steady 'avail_mb')
        store_mb         = Get-Median (Get-Col $steady 'compression_ws_mb')
        page_writes_s    = Get-Mean (Get-Col $idle 'page_writes_s')
        store_cpu_s      = Get-Delta $active 'compression_cpu_s'
        engine_cpu_s     = Get-Delta $active 'engine_cpu_s'
        hog_alloc_s      = $hogAlloc
        hog_chunk_p95_ms = $hogP95
        hog_retouch_ms   = $hogRetouch
        pressure_reads_s = if ($press.Count) { Get-Mean (Get-Col $press 'page_reads_s') } else { $null }
    }
}
$cells = @($cells | Sort-Object arm, run)

$metricNames = @('private_ws_mb', 'avail_mb', 'store_mb', 'page_writes_s', 'store_cpu_s', 'engine_cpu_s',
    'hog_alloc_s', 'hog_chunk_p95_ms', 'hog_retouch_ms', 'pressure_reads_s')

# cell table, invariant culture
$cellCols = @('run', 'arm') + $metricNames
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add(($cellCols -join ','))
foreach ($c in $cells) {
    $d = [ordered]@{}; foreach ($k in $cellCols) { $d[$k] = $c.$k }
    $lines.Add((ConvertTo-InvCsvLine -Row $d -Columns $cellCols))
}
[IO.File]::WriteAllLines((Join-Path $ResultsDir 'summary-cells.csv'), $lines)

# per-arm table
$rng = New-Object System.Random($BootstrapSeed)
$fmt = { param($v) if ($null -eq $v) { 'n/a' } else { ([math]::Round([double]$v, 1)).ToString($inv) } }
$md = New-Object System.Collections.Generic.List[string]
$md.Add('| Metric | Arm | n | Median | 95% CI of median | p95 |')
$md.Add('|---|---|---|---|---|---|')
foreach ($m in $metricNames) {
    foreach ($g in ($cells | Group-Object arm | Sort-Object Name)) {
        $vals = [double[]]@($g.Group | ForEach-Object { $_.$m } | Where-Object { $null -ne $_ })
        if ($vals.Count -eq 0) { continue }
        $ci = Get-BootstrapCi $vals $BootstrapResamples $rng
        $ciText = if ($null -eq $ci[0]) { 'n/a' } else { '{0} to {1}' -f (& $fmt $ci[0]), (& $fmt $ci[1]) }
        $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $m, $g.Name, $vals.Count, (& $fmt (Get-Median $vals)), $ciText, (& $fmt (Get-Quantile $vals 0.95))))
    }
}
# paired differences against the "none" arm, matched by run (each run is a block with all arms)
$baseline = @{}
foreach ($c in ($cells | Where-Object arm -eq 'none')) { $baseline[$c.run] = $c }
if ($baseline.Count -gt 0) {
    $md.Add('')
    $md.Add('Paired differences versus the "none" arm (same run):')
    $md.Add('')
    $md.Add('| Metric | Arm minus none | n pairs | Median difference | 95% CI of median difference |')
    $md.Add('|---|---|---|---|---|')
    foreach ($m in $metricNames) {
        foreach ($g in ($cells | Where-Object arm -ne 'none' | Group-Object arm | Sort-Object Name)) {
            $diffs = [double[]]@(foreach ($c in $g.Group) {
                    if ($baseline.ContainsKey($c.run) -and $null -ne $c.$m -and $null -ne $baseline[$c.run].$m) { [double]$c.$m - [double]$baseline[$c.run].$m }
                })
            if ($diffs.Count -eq 0) { continue }
            $ci = Get-BootstrapCi $diffs $BootstrapResamples $rng
            $ciText = if ($null -eq $ci[0]) { 'n/a' } else { '{0} to {1}' -f (& $fmt $ci[0]), (& $fmt $ci[1]) }
            $md.Add(('| {0} | {1} | {2} | {3} | {4} |' -f $m, $g.Name, $diffs.Count, (& $fmt (Get-Median $diffs)), $ciText))
        }
    }
}
[IO.File]::WriteAllLines((Join-Path $ResultsDir 'summary.md'), $md)
$md
