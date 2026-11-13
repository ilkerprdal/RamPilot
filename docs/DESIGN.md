# RamPilot — Design

RamPilot lowers the resident memory of **idle background desktop apps** (Electron / WebView2: WhatsApp, Discord,
Slack, Teams, ...) by putting a **feedback-controlled hard working-set maximum** on their processes. It is
**not** an `EmptyWorkingSet` "RAM cleaner": it never trims a process to a minimum, it walks a cap down in small
steps while the measured cost stays under a budget, remembers what each process tolerates, and does less (or
nothing deep) when the machine has memory to spare.

Status: Windows 11 x64, per-user, no admin, no driver, no code injection, no network access.

## 1. Problem and idea

Each Electron/WebView2 app ships its own Chromium. Idle in the background, most of its working set is **cold**
(caches, old DOM/JS heap, start-up code). Windows only trims those pages under memory pressure, coarsely and late.

Windows already compresses memory (the Memory Compression store). RamPilot decides **when, from which process,
and how much**. Inspiration: Meta's TMO / senpai, which tunes cgroup `memory.high` from a pressure signal; Windows has
no per-process PSI, so RamPilot estimates stall cost from fault counters.

**Mechanism.** `SetProcessWorkingSetSizeEx(h, min, max, QUOTA_LIMITS_HARDWS_MAX_ENABLE)`. When the hard maximum is
below the working set, the kernel's working-set manager chooses which pages leave. Measured ([experiments/README.md](../experiments/README.md),
probe A): the **first** trim under a new hard maximum evicts in working-set insertion order (oldest first), not by
access, unless the process has been idle for about 5 minutes so that its pages are aged; **once capped**, replacement
is access-aware (recency). The policy is built around that (§3). Dirty private pages go to the modified list and then to the compression store (about a third of
their size or less in RAM); clean file-backed pages go to the standby list (immediately available). The process keeps
running; touching an evicted page is a soft fault (standby / store) or, rarely, a hard fault (page file).

## 2. Architecture

```
src/RamPilot.Core     pure policy, no P/Invoke, no I/O, no clock      ← unit-tested (tests/RamPilot.Core.Tests)
  Controller          Decide(state, sample, config, context) → action
  Governor            memory pressure → aggressiveness
  Savings             reclaimed / store cost / net estimators
  ProcessSnapshot     process table + FindAppRoot (PID-reuse safe)
  RoleClassifier      Chromium role from the command line, fail-closed exclusion
  DenyList            built-in, non-overridable deny list
  EngineConfig        config.json model + clamping
  LearnedProfiles     persisted per-(app, exe, role) caps and floors
src/RamPilot.Engine   thin Win32 shell (AssemblyName RamPilot, Native AOT single exe)
  Win32/ProcessSampler   NtQuerySystemInformation snapshot + bounds checks + startup self-check
  Win32/ProcessHandle    handle bound to (pid, create time), cap / uncap, command line
  Win32/FocusTracker     WinEvent hook + per-tick poll + ApplicationFrameHost resolution
  Win32/AudioSessions    IAudioSessionManager2 via source-generated COM ([GeneratedComInterface])
  Safety/Journal         crash journal, written before any cap
  Safety/Guardian        watchdog process + leftover scanner
  Ipc/                   protocol v2 server (docs/IPC.md)
  Engine.cs              main-loop tick: sample → decide → apply → publish
  Tray.cs                notification-area icon (en/tr)
src/RamPilot.Panel    WebView2 control panel (separate project, IPC v2 client)
```

**Threads.** One main loop owns all state. The focus hook, tray and IPC run on their own threads and only post
closures to an inbox (`Engine.Post`); the main loop runs them. Every shutdown path (tray Quit, IPC `quit`, Ctrl+C,
SIGTERM, `ProcessExit`, unhandled exception on another thread) signals the main loop and waits (bounded) for it to
release the caps; nothing touches the process list from a foreign thread.

**Cadence.** Sampling is on a **fixed cadence** (`tickSeconds`, default 2 s). Wake-ups from a focus event or the inbox
do not sample: a focus event releases the newly focused app immediately (no measurement involved), an inbox item
runs, then the loop goes back to waiting for the next tick deadline. Missed ticks are skipped, not replayed.

## 3. The controller

Per process, three states: `Free` (no cap), `Descending`, `Holding`. Inputs per tick: working set *W*, private WS,
commit, page-fault delta *f*, hard-fault delta *h*, CPU cycles; context: focus, audio, pause, disabled, aggressiveness
*a* from the governor, grace, and whether the kernel still reports our hard maximum.

### Releases (always first, valid sample not required)
paused, disabled, focused, active audio session (+`audioHoldSeconds`), **sustained** CPU activity
(+`activeHoldSeconds`).

**Sustained, not momentary.** A sample counts as busy when the process runs above `activeMcyclesPerSec` (150 ≈ 4 % of
a core). Activity is the exponentially weighted share of busy time, *b ← b + α(busy − b)* with α = 1 − e^(−Δt/τ) and
τ = `activeWindowSeconds` (60); the process is active when *b* ≥ `sustainedActiveFraction` (0.5). A 30 s burst from
rest reaches 1 − e^(−0.5) ≈ 0.39; continuous load crosses 0.5 after ≈ 42 s. Reason: background chat apps run short
notification/sync bursts. Measured on idle, unfocused WhatsApp (engine metrics CSV, 2026-11-13): 10–30 s bursts every
2–3 min, renderer peaks up to ~1,400 Mcycles/s, averages 98 (renderer) / 64 (main) Mcycles/s. An earlier version treated
any busy sample as activity; it never reached 300 s of idleness and so never managed the apps it targets. Bursts touch
the hot set, not the cold pages that the first-cap rule protects; if a burst does need evicted pages, the refault-cost
rule raises the cap within one tick (and records a floor). Burst samples are also excluded from the fault baseline.
Calls and video are caught immediately by audio-session detection.

### When a process may be capped
* **First cap of a process:** only after `firstCapIdleSeconds` (300) of idleness — unfocused, no sustained CPU activity,
  no audio session; short bursts do not reset the clock — and when the process is at least `minProcessAgeSeconds` (300)
  old. A process first seen by the engine counts as busy at that moment. Data: with 30, 60 and 120 s of idle before the cap, a 150 MB
  cap kept 37 % of hot and cold pages alike and ≤ 0.3 % of a contiguous hot block (9/9 runs); after ≥ 300 s idle the hot
  set survived (100 % in 5 of 6 runs, 73.5 % in one) ([experiments/README.md](../experiments/README.md), probe A).
* **Re-entry** (the process was capped before and is aged): `focusGraceSeconds` (60) after the last focus, plus the
  activity/audio holds.

### Cost signal (estimated stall budget)
A fault only counts as a *refault* while the process is at its cap (`W ≥ cap × atCapRatio`, 0.95), because only then
does a new page force an eviction. `PageFaultCount` counts every refault exactly once — from standby, compression store
or disk — but it also counts demand-zero faults of new allocations (1.002 per page). The app's own fault rate is
therefore learned while it is uncapped **and idle** — not during the focus grace, the activity hold or the audio hold —
(EWMA, ≤ 500/s) and subtracted. While capped it cannot be re-measured, so it **decays** with a 10-minute half-life:
a stale baseline cannot hide refaults for long, and an allocation-heavy app is eventually judged conservatively
(its cap is raised, never lowered, by this). Per tick:

```
refaults = max(0, f − baseline × dt)            only while W ≥ 0.95 × cap
cost_us  = refaults × refaultCostUs (30)  +  h × hardFaultCostUs (2000)
budget   = stallBudget (0.001 = 1 ms/s) × (0.5 + a)
```

`HardFaultCount` (*h*) does **not** see compression-store reads (0 across 884,736 store refaults in probe B); it counts
file reads only (one per 16-page read I/O). It is kept as a separate disk-I/O term; nothing relies on it to detect
store refaults.

* Spike: `cost_us / 1e6 / dt > budget × spikeMultiple (10)` → **raise immediately**.
* Window (`stepSeconds` = 30 s, or `fastStepSeconds` = 10 s while walking back to a learned cap):
  `frac = Σcost / 1e6 / Σdt`. `frac > budget` → raise; `frac ≤ budget × descendHeadroom (0.5)` → the window is clean.

**Measured vs. budgeted cost.** Probe B measured the CPU cost per refaulted page: ~0.8 µs from standby, ~1.65 µs from
the compression store (≈ 14 µs per 16-page read), ~7 µs averaged from NVMe (≈ 100 µs per 64 KB read). The default
`refaultCostUs` = 30 is deliberately higher. It budgets *user-visible* stall rather than CPU time: refault bursts land on
the UI thread exactly when the user returns, they add cache/TLB refill, and slower disks or a busy store cost more.
Erring high only costs savings (descent stops earlier); erring low costs latency. `hardFaultCostUs` stays at 2 ms for
the same reason (HDDs, contended I/O).

### Descent (no slam)
```
first cap  = max(W × startRatio (0.9), L)
next cap   = max(cap × s(a), L)             s(a) = 1 − (1 − stepFactor) × (0.5 + 0.5a),  stepFactor = 0.85
L (lower bound) = max(minCapMb, floor, depthLimit)
depthLimit = Wref × (1 − a × maxDepth)        Wref = W when the descent started
```
A step is taken only after a clean window; the cap that survived the window becomes the **learned cap** (last known
good). When no step is possible (or the window was within budget but not clean) the state becomes `Holding`.
The first step is the gentle one (≤ 10 % of W) because it is the only application that can still evict by insertion
order; once capped, replacement follows recency, and probe A3 kept 94–100 % of the hot set at every ×0.85 step after the
first. No single trim removes more than 10 % of the working set at entry or `1 − s(a)` (≤ 15 %) per later step.

### Raise and floor (ssthresh)
On a raise at cap *C*: `floor = max(floor, C × floorMargin (1.1))`, `cap = max(C × increaseFactor (1.5), floor)`,
state `Holding`. Later descents stop at the floor, so a periodic GC that touches cold pages produces at most a few
raises while the floor converges above the GC working set — not an endless sawtooth. Floors expire after
`floorTtlHours` (24 h) so a changed app is re-learned.

### Holding
Holding re-tests one step lower after `holdRetestMinutes` (15) of clean windows if there is room above *L*, and
resumes descending at once if the governor's aggressiveness rose by more than 0.1 since holding began.
When pressure falls, existing caps are neither raised nor released; only further descent is limited. Holding a cap
that costs less than the budget is free, and releasing it would let cold pages regrow for nothing.

### Tampering
If the target clears our hard limit itself (its own `SetProcessWorkingSetSize`), the cap is re-applied — but never
as a large trim: if the working set regrew, the re-applied cap is `max(cap, W × startRatio)` and the old cap is walked
down to with fast steps. If the limit is cleared 3 times within 10 minutes, RamPilot stops fighting the app, releases
it (`cap_released`, reason `error`) and leaves it alone for 30 minutes.

### Re-entry (no re-probe)
After focus/activity/audio, the process re-enters with its learned cap *Lc*:
`target = max(Lc, floor, depthLimit(W))`, where the reference WS is refreshed to the current *W* so that a stored
reference from a smaller app cannot bypass the governor's depth limit; `cap = max(target, W × startRatio (0.9))`. If `cap > target` the remaining
distance is walked with fast steps (≤ 15 % each, 10 s windows, still cost-checked); reaching the target returns to
`Holding`. If the app shrank below what was learned, a fresh gentle descent starts instead. Why steps rather than a
second idle period: the re-entering process was aged before its first cap, and probe A3 shows bounded steps keep the
hot set; waiting another 5 minutes would leave the regrown memory resident for no measured benefit. **This is an
assumption:** no probe has measured the selectivity of a re-cap after a release and regrowth (§8). The 10 % first step
and the cost check bound the damage if it is wrong (a refault burst, then a raise). A persisted profile on a *new*
process instance is still a first cap, so the 300 s idle rule applies to it.

### Persistence
Learned cap, reference WS and floor are stored per `(app exe, image, role)` in `%LOCALAPPDATA%\RamPilot\learned.json`
(dropped after 30 days). Role is the Chromium `--type` (utility processes include `--utility-sub-type`). Several live
processes can share a key (two renderers); last writer wins — a profile is only a bounded hint (every restore is
capped by the current WS and still cost-checked).

### Invariants (unit-tested)
never cap a focused or audio app; no first cap before 300 s idle (after focus, sustained CPU activity or audio) or before the
process is 300 s old; never below `minCapMb` or the floor; release on pause/disable/shutdown; first cap ≥ 0.9 W; every
lowering ≥ `stepFactor` × previous; restore ≥ 0.9 W, then bounded steps; the uncapped fault baseline is not counted as
refaults, it is learned only while idle and decays while capped; zero aggressiveness → no cap; grace freezes cost
decisions; tampered caps are re-applied gently, then given up after 3 tampers in 10 minutes; a restore refreshes the
reference WS. The simulated process
(`tests/.../SimulatedProcess.cs`) has a hot set, a GC region touched every 120 s and cold memory, with recency (LRU)
eviction under the cap — i.e. it models the *capped* regime; the first-cap regime is handled by the idle rule.

## 4. Governor (demand awareness)

With plenty of free memory, compressing idle pages costs CPU and buys nothing; Windows trims anyway when memory gets
tight. The governor turns system state into an aggressiveness *a*:

| level | condition (any) | a (default) |
|---|---|---|
| high | low-memory resource notification signaled, available < 10 %, commit > 90 % | 1.0 |
| medium | available < 25 %, commit > 80 % | 0.75 |
| low | otherwise | 0.4 |

Escalation is immediate; de-escalation requires the lower level for `deescalateSeconds` (60). *a* scales the depth
limit (at low pressure caps stop at 60 % of the reference WS), the step size and the stall budget.
`governor.pressureOverride` forces a level for benchmarks. Not yet included: pagefile-write back-off,
fullscreen/game and battery signals (§7).

## 5. Safety model

1. **Allowlist only.** Only process trees whose root image is in `config.json → apps` are touched; helpers
   (`msedgewebview2.exe`) only below such a root. `FindAppRoot` stops at an ancestor created after its child
   (recycled PID).
2. **Built-in deny list** (`DenyList.cs`): anti-cheat, game clients/launchers, security software, Windows-critical
   processes, RamPilot itself, and game install paths (`\steamapps\`, `\Epic Games\`, ...). Applied when the config is
   loaded and again before a process is opened; the user cannot override it.
3. **Fail-closed exclusion.** GPU, audio-service and crashpad processes are recognised by their command line, read with
   a growable buffer (Chromium command lines often exceed 8 KB). If it cannot be read, the process is excluded.
4. **Journal before cap.** Before the first cap on a process instance, `(pid, create time, exe)` is written to
   `journal.txt` and flushed to disk; if that fails the cap is not applied.
5. **Guardian.** The engine launches `RamPilot.exe --guardian <enginePid>`. It verifies the PID is a RamPilot image,
   waits on the engine's process handle and, when the engine exits for any reason (including `TerminateProcess`),
   clears `HARDWS_MAX` on every allowlisted process (and journaled instance) and exits. Caps are only tightened while
   the guardian is alive; if it dies it is restarted every 10 s. **If it stays down for 30 s** (three attempts — exe
   removed, blocked by antivirus), the engine releases every cap and suspends capping until a guardian runs again, so
   no cap is ever held unguarded for long.
   *Fast engine restart while an old guardian is alive:* the old guardian wakes when its engine dies. A new engine can
   only pass the single-instance mutex after that death, and it clears leftovers at its own start, before any cap of
   its own (which also waits for the 300 s idle rule). So when the old guardian finds the engine mutex owned by a new
   instance, it exits without scanning and never touches the new engine's caps.
6. **Startup scan.** On start, any allowlisted process with `QUOTA_LIMITS_HARDWS_MAX_ENABLE` set is cleared
   (event `leftover_cleared`). `--recover` runs the same scan alone. This also clears hard limits that **another tool or
   the app itself** set on an allowlisted process; RamPilot cannot tell them apart from its own leftovers after a
   crash. That is acceptable: the scan only ever removes a limit (never adds or lowers one), only on apps the user put
   on the allowlist for RamPilot to manage, and a hard working-set maximum is not something these apps set for
   correctness. A user who manages an app's limits with another tool should not allowlist it here.
   A release that fails keeps its journal entry and is retried every tick until the limit is gone.
7. **Uncap without trimming.** Removing a hard limit with a small soft maximum trims immediately (experiments, phase
   0), so the soft maximum is set to `max(original, 2 × WS, 64 MB)`.
8. **Process-table self-check.** The `SYSTEM_PROCESS_INFORMATION` offsets are undocumented. Every snapshot is
   bounds-checked (entry size, thread array, name pointer inside the buffer); a failed snapshot is "no information",
   never "all processes exited". At startup the engine compares its own entry with `GetProcessMemoryInfo`,
   `GetProcessTimes`, `ProcessIdToSessionId` and `QueryProcessCycleTime` and **refuses to run** on mismatch.
9. **Robust loop.** A tick that throws (I/O, interop) is logged and skipped; CSV/journal/profile write errors never stop
   the engine. Sleep/resume is detected as a tick gap > 3 × tick; deltas across the gap are discarded and caps are
   held for 60 s.
10. **IPC.** Per-session pipe `\\.\pipe\RamPilot.<session>`, `CurrentUserOnly`, ≤ 4 instances, lines ≤ 4 KiB (longer
    closes the connection), 2-minute idle timeout, 10 s write timeout (a client that stops reading loses its slot), no diagnostic back doors. `setApp` only accepts allowlisted images.

## 6. Savings estimation (honest numbers)

Reported separately, all ≥ 0 (IPC `savings`):

* **reclaimed** = Σ over managed processes of `U − P`, where `P` is the current private WS and `U` the counterfactual
  private WS without a cap, estimated from the process's own trajectory:
  `U = P_entry × min(1, C_now / C_entry)` with `C` = commit charge (unaffected by working-set limits). If the app frees
  memory itself, `U` falls with its commit, so its own shrinking is not credited; growth is not credited either
  (conservative). Shared pages are excluded by using *private* WS. The baseline is reset at every entry, and a
  released process contributes nothing.
* **storeCost** = reclaimed × r. `r` is measured: for each trim, the private bytes RamPilot trimmed vs. the growth of
  the Memory Compression process's working set over the next ~30 s (EWMA with a 0.35 prior, clamped to [0.25, 1]).
  Other processes also feed the store (bias towards higher cost). The lower clamp exists because dirty pages can sit
  on the modified list — still resident, uncompressed — before the store absorbs them.
* **net** = reclaimed − storeCost; this is the headline number (tray tooltip, panel).

Cross-check: `system.availMb` and the metrics CSV (`%LOCALAPPDATA%\RamPilot\metrics_*.csv`) allow comparing net with
the change in available memory, which is the system-level ground truth but noisy.

## 7. Known limitations

* `PageFaultCount` cannot tell demand-zero, standby, store and file faults apart. The uncapped baseline is subtracted,
  but an allocation burst while capped still reads as refaults (conservative: it raises the cap).
* The first trim of a process evicts by insertion order unless it has been idle ~5 min (§8). The 300 s rule covers the
  measured case (5 of 6 runs fully selective, one 73.5 %); a process with an unusual access pattern can still lose some
  hot pages once, which shows up as a one-time refault burst (~7,700 faults for 30 MB, 6–13 ms CPU) and a raise.
* Modified-list residency is not observable without admin rights, so `storeCost` uses a floor ratio (above).
* Learned profiles share a key across processes with the same (app, exe, role).
* No counterfactual holdout yet (a rotating unmanaged app would give a measured baseline instead of an estimate).
* Governor: no page-file write back-off, no fullscreen/game or battery input yet.
* No predictive unlock on Alt-Tab start; the first frames after focusing a capped app may fault pages back in.
* "End process tree" in Task Manager kills the guardian too; the next engine start (or `--recover`) clears leftovers,
  and limits also disappear when the target process exits.
* x64 only (offsets); ARM64 is untested.
* Learned profiles and logs contain process names; they never leave the machine.

## 8. Verified vs. assumed

| Claim | Status |
|---|---|
| A hard max below WS trims to the cap immediately; idle WhatsApp's renderer barely refaults | verified (experiments/README.md, phases 0–1; live runs in tests/live) |
| Uncapping with a small soft max trims; `max(orig, 2×WS, 64 MB)` does not | verified (phase 0) |
| NtQSI x64 offsets | verified at every start by the self-check |
| Guardian clears caps after `Stop-Process -Force` of the engine | verified (tests/live evidence file) |
| Audio sessions detected per PID (render + capture) | verified by `--summary` while a sound plays |
| Re-cap after release and regrowth is as selective as a step while capped | **assumed** (not probed; the 10 % first restore step and the cost check bound the risk, §3) |
| Victim selection under a hard max | **measured: insertion-order on first cap, recency afterwards** ([experiments/README.md](../experiments/README.md), probe A, raw data in `experiments/results/kernel-probe/`). First cap after 30/60/120 s idle: hot and cold kept alike (37 %), contiguous hot block ≤ 0.3 %; after ≥ 300 s idle: hot set kept (100 % in 5/6, 73.5 % in 1). While capped: hot set back within 1 s (~7,700 faults), then 0 faults; ×0.85 steps kept 94–100 %. Consistent with the engine's live runs (tests/live/evidence-2026-11-13.txt): young synthetic process lost hot pages, the same process 10 min later trimmed 284 → 200 MB with 0 refaults |
| Store reads are counted in `HardFaultCount` | **measured: no** (0 across 884,736 store refaults, probe B). `PageFaultCount` counts each refault once; the controller uses it |
| Cost per refault | **measured** ~0.8 µs standby, ~1.65 µs store, ~7 µs NVMe (probe B); defaults (30 µs / 2 ms) are deliberately conservative budgets (§3) |
| Savings estimator tracks the true counterfactual | **assumed**; no holdout yet |
