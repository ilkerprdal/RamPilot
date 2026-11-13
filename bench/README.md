# Benchmark methodology

This document specifies how RamPilot's claims are tested. It is written before the results, on purpose: the
hypotheses, metrics, analysis and exclusion rules are fixed here so that the results cannot shape them.

**Status:** the methodology is complete; the harness in [`scripts/`](scripts/) covers arm orchestration,
metric sampling, the `EmptyWorkingSet` baseline, the memory-pressure scenario and the statistics. The
focus-latency probe is specified below but **not implemented yet**. No benchmark results have been published.
The preliminary single-machine numbers in the main README come from [`experiments/`](../experiments/README.md),
not from this benchmark.

## 1. Claims under test

| ID | Claim | Primary metric | Supported if |
|---|---|---|---|
| H1 | RamPilot lowers the resident memory of idle managed apps. | Steady idle private WS of target trees, paired difference vs. `none` | 95% CI of the median difference is below 0 and its magnitude ≥ 50 MB |
| H2 | It does so with a smaller focus-latency penalty than a RAM cleaner. | Time to first changed frame after Alt-Tab, p95 | RamPilot p95 < cleaner p95, CIs not overlapping; RamPilot median within one frame of `none` |
| H3 | Under memory pressure, proactive reclaim beats reactive reclaim. | Hog allocation time, and focus latency right after pressure | RamPilot faster than `none` on both, CIs of paired difference excluding 0 |
| H4 | Its own overhead is small. | Engine CPU seconds and private WS; Memory Compression CPU seconds | Engine < 1% of one core on average, < 15 MB private WS; store CPU reported, no threshold |

Secondary metrics are reported but not used for these decisions. A claim that is not supported is reported
as not supported.

## 2. Arms

| Arm | What runs during the active phases |
|---|---|
| `none` | Nothing. Windows default memory management. |
| `rampilot` | `RamPilot.exe --minutes N` with default configuration (record the config file hash in the report). |
| `cleaner` | [`Invoke-EmptyWorkingSetLoop.ps1`](scripts/Invoke-EmptyWorkingSetLoop.ps1): `EmptyWorkingSet` on the same target trees every 300 s, the way typical RAM cleaners behave. |

All arms act on the same target set: the allowlisted apps' root processes and all their descendants.

## 3. Fixed conditions

Record each item in the report; keep it identical across arms and runs.

- **Machine:** one physical machine per result set. Record CPU, RAM, storage, Windows build, pagefile
  configuration and `Get-MMAgent` output (the harness writes most of this to `manifest.json`).
- **Apps:** fixed versions, auto-update disabled for the duration, same accounts and same chat state
  (no new messages expected: use test accounts or mute everything). Record versions.
- **Power:** on AC power, fixed power plan, display never sleeps, no battery saver.
- **Background noise:** Windows Update paused, no scheduled scans during the window, OneDrive and similar
  sync paused, no other user activity. Leave the machine alone during a cell.
- **Display:** fixed resolution and refresh rate (record it; it quantizes latency to one frame interval).
- **Start state:** reboot, sign in, start the target apps in a fixed order, minimize them to the background,
  then start the harness.

## 4. Phases of one cell

One *cell* is one run of one arm.

| Phase | Default length | Arm active | Purpose |
|---|---|---|---|
| `warmup` | 600 s | no | Apps reach a steady background state after start |
| `idle` | 1800 s | yes | Main measurement; the last half is "steady idle" |
| `focus` | 5 probes, 180 s apart | yes | Focus-latency probes (not automated yet) |
| `pressure` | until the hog reaches its target, then 300 s hold | yes | Memory-pressure scenario |
| `cooldown` | 120 s | no | Recovery after the arm stops |

## 5. Independence and order

- **Runs:** at least 10 runs; each run contains every arm once (randomized complete block design).
- **Order:** arm order is shuffled independently within each run with a recorded seed
  (`Invoke-BenchArms.ps1 -Seed`). Running arms in a fixed order would confound the arm with time since boot.
- **Reboot between cells** is the reference protocol, because the compression store, standby list and app
  heaps carry state from one cell to the next. The harness supports it: run one cell per boot with
  `-Resume -MaxCells 1`, for example from a logon task you create, after the fixed start procedure. If you do
  not reboot between cells, say so in the report; cooldown alone does not reset the system.
- **No peeking:** do not stop early because results look good or bad. Run the planned number of runs.

## 6. Focus latency (specified, not implemented)

Goal: the delay a user feels when switching to a background app.

1. Before each probe, the target window is second in the Alt-Tab order (activate it once, then activate a
   neutral window such as an empty Notepad).
2. The probe sends Alt+Tab with `SendInput` and records the QPC timestamp `t0`.
3. A capture session on the target's monitor using `Windows.Graphics.Capture` timestamps every frame. `t1` is
   the first frame after `t0` whose content differs from the pre-switch frame in the target window's
   rectangle by more than a small pixel threshold (to ignore cursor blink). Time to first changed frame is
   `t1 - t0`. A second measurement sends a harmless key that changes the view (Page Up in the message list)
   and records time to the next changed frame. **Never send text or Enter to a chat app.**
4. Alternative where Windows.Graphics.Capture is not suitable: PresentMon (ETW) present events of the target
   process. ETW needs administrator rights; that is acceptable for the benchmark only, never for RamPilot.
5. Also record, for the 5 s after `t0`: page faults and hard faults of the target tree (from ETW or from the
   RamPilot CSV in the `rampilot` arm) and system page reads/s.
6. Five probes per cell, 180 s apart, so caps and cleaner passes can reapply between probes. The animation of
   Alt-Tab is part of every arm, so it cancels out in differences.

Latency is reported in milliseconds together with the frame interval; differences below one frame interval
are reported as "within one frame".

## 7. Memory-pressure scenario

[`Start-MemoryHog.ps1`](scripts/Start-MemoryHog.ps1) allocates and writes 64 MB chunks until available memory
falls to a target (default 1024 MB), holds it, writes every page again, then releases it. It stops at
`-MaxMB`, refuses targets below 512 MB unless forced, and stops if commit headroom falls below 1 GB.

What it measures: when RAM runs out, Windows must trim, compress or page out other processes before the hog
can get memory. If RamPilot already moved cold pages out, that work has been done in advance and the hog
should allocate faster, with fewer hard faults for everything else. The `cleaner` arm shows what happens when
the trimming is done blindly.

Recorded: time to reach the target, time per chunk (median, p95), time to rewrite all memory after the hold,
system page reads/s and writes/s, target-tree working set during pressure, and focus latency right after the
hold (once implemented).

## 8. Metrics

Sampled by [`Measure-Memory.ps1`](scripts/Measure-Memory.ps1) and the orchestrator every 5 s, using
CIM performance classes (`Win32_PerfFormattedData_PerfOS_Memory`, `Win32_PerfFormattedData_PerfProc_Process`)
and `GlobalMemoryStatusEx`. No `Get-Counter`, no admin.

| Column | Source | Unit |
|---|---|---|
| `avail_mb` | `GlobalMemoryStatusEx.ullAvailPhys` (free + zeroed + standby) | MB |
| `committed_mb`, `commit_limit_mb` | PerfOS_Memory | MB |
| `standby_mb`, `modified_mb` | PerfOS_Memory (standby core + normal + reserve; modified list) | MB |
| `page_reads_s`, `page_writes_s` | PerfOS_Memory: hard-fault read and pagefile write operations | 1/s |
| `pages_input_s`, `pages_output_s` | PerfOS_Memory: pages read from / written to disk | pages/s |
| `compression_ws_mb` | Working set of the `Memory Compression` process (size of the store in RAM) | MB |
| `compression_cpu_s` | CPU time of `Memory Compression` (cumulative) | s |
| `target_procs` | Number of processes in the target trees | count |
| `target_ws_mb`, `target_private_ws_mb` | Sum over target trees, PerfProc_Process | MB |
| `target_faults_s` | Sum of page faults/s (soft + hard) over target trees | 1/s |
| `target_cpu_pct_core` | Sum of % processor time over target trees (100 = one core) | % |
| `engine_private_ws_mb`, `engine_cpu_s` | RamPilot engine process, if running | MB, s |

Known limitations:

- Formatted CIM rates are computed by the WMI provider between its own samples; the first sample after
  start-up can read 0. The steady-state analysis window excludes the start of each phase.
- Per-process hard faults are not exposed by these classes. System `page_reads_s` is the proxy; per-process
  hard faults come from the RamPilot CSV (rampilot arm) or ETW.
- Private working set is resident private memory. It does not include the compressed copy of trimmed pages,
  which is why `compression_ws_mb` is always reported next to it.
- Net effect on the system is best read from `avail_mb` and `compression_ws_mb` together, not from the target
  working set alone.

## 9. Analysis

Per cell, one value per metric (computed by [`Get-BenchSummary.ps1`](scripts/Get-BenchSummary.ps1)):

| Cell metric | Definition |
|---|---|
| `private_ws_mb` | Median of `target_private_ws_mb` over steady idle (last half of `idle`) |
| `avail_mb` | Median of `avail_mb` over steady idle |
| `store_mb` | Median of `compression_ws_mb` over steady idle |
| `page_writes_s` | Mean of `page_writes_s` over `idle` |
| `store_cpu_s`, `engine_cpu_s` | CPU seconds used over `idle` + `pressure` |
| `hog_alloc_s`, `hog_chunk_p95_ms`, `hog_retouch_ms` | From the hog log of that cell |
| `pressure_reads_s` | Mean of `page_reads_s` during `pressure` |
| focus latency (planned) | Median and maximum of the 5 probes |

Across runs:

- Per arm: n, **median**, **p95**, and a **95% bootstrap confidence interval of the median** (percentile
  method, 2000 resamples, fixed seed).
- **Paired differences** per run against `none` (`rampilot − none`, `cleaner − none`), with the median and
  its bootstrap CI. Pairing by run removes between-run drift. The decisions in section 1 use these.
- With 10 runs, percentile CIs are wide; that is the honest state of the evidence. More runs narrow them.
- **Exclusions** are allowed only for: a target app crashed, updated or showed a dialog during the cell; the
  machine was used during the cell; the harness failed. Excluded cells are listed in the report with the
  reason, and the whole run is repeated rather than the single cell.
- **All** completed runs are reported, including runs where RamPilot does worse.

## 10. Running it

```powershell
cd bench\scripts

# 1. Smoke test (about a minute). Starts a dummy process and touches nothing else.
.\Test-Harness.ps1

# 2. Benchmark, default phases (about 50 minutes per cell, 30 cells for 10 runs x 3 arms)
.\Invoke-BenchArms.ps1 -TargetName WhatsApp.Root.exe, Discord.exe -RamPilotPath C:\Tools\RamPilot\RamPilot.exe

# 2b. Same, one cell per boot
.\Invoke-BenchArms.ps1 -TargetName WhatsApp.Root.exe -RamPilotPath C:\Tools\RamPilot\RamPilot.exe -OutDir ..\results\local-main -MaxCells 1
.\Invoke-BenchArms.ps1 -TargetName WhatsApp.Root.exe -RamPilotPath C:\Tools\RamPilot\RamPilot.exe -OutDir ..\results\local-main -Resume -MaxCells 1   # after each reboot

# 3. Summary
.\Get-BenchSummary.ps1 -ResultsDir ..\results\local-main
```

Scripts:

| Script | Role |
|---|---|
| `Invoke-BenchArms.ps1` | Orchestrates arms, phases, randomized order, resume; writes `schedule.csv`, `manifest.json`, `samples\`, `logs\` |
| `Measure-Memory.ps1` | Stand-alone sampler to CSV |
| `Invoke-EmptyWorkingSetLoop.ps1` | The `cleaner` baseline |
| `Start-MemoryHog.ps1` | Memory-pressure generator |
| `Get-BenchSummary.ps1` | Per-cell metrics, per-arm statistics, paired differences |
| `Start-DummyTarget.ps1`, `Test-Harness.ps1` | Harness self-test without real apps |
| `BenchCommon.ps1` | Shared functions (target-tree resolution, sampling, invariant-culture CSV) |

All scripts run on Windows PowerShell 5.1 and PowerShell 7 without administrator rights. CSV numbers use a
dot as decimal separator regardless of the system locale.

**Safety.** The harness never caps anything itself. The cleaner baseline needs an explicit target, refuses
system, security, shell and harness processes (and their child trees), skips such processes inside a target
tree, and aborts if a target tree is implausibly large. The rampilot arm stops the engine with the IPC `quit`
command, then runs `RamPilot.exe --recover`.

**Privacy.** `manifest.json` and the CSVs contain executable names and versions of the target apps and
machine specifications, but no user or host names. Output under `bench/results/local-*` is ignored by git;
review it before publishing.

## 11. Reporting template

Copy this into `bench/results/<date>-<machine-label>/REPORT.md` and fill it in. Publish the raw CSVs, the
manifest, the schedule and the exact commit of RamPilot and of this harness next to it.

```markdown
# RamPilot benchmark — <date>, <machine label>

## Setup
- RamPilot: <version>, commit <sha>, config SHA-256 <hash> (default: yes/no)
- Harness: commit <sha>
- Machine: <CPU>, <RAM> GB, <storage>, Windows <edition> build <build>, pagefile <config>,
  memory compression <on/off>, page combining <on/off>, display <res> @ <Hz>
- Apps: <name version>, ...
- Phases: warmup <s>, idle <s>, focus <probes x interval>, pressure hold <s>, cooldown <s>; sample interval <s>
- Runs: <n> x arms <list>; seed <seed>; reboot between cells: <yes/no>
- Deviations from bench/README.md: <none / list>

## Results
<paste the per-arm table and the paired-difference table from summary.md>

| Claim | Result | Evidence |
|---|---|---|
| H1 | supported / not supported / inconclusive | median difference <x> MB, 95% CI <a> to <b> |
| H2 | ... | ... |
| H3 | ... | ... |
| H4 | ... | ... |

## Exclusions
<cell, reason> or "none"

## Negative and unexpected results
<everything that went against RamPilot or the hypotheses>

## Raw data
samples/, logs/, manifest.json, schedule.csv, summary-cells.csv
```
