# RamPilot experiments

Measurements behind RamPilot's design. Everything here is reproducible; raw data is in [`results/`](results/).
Negative and surprising results are reported as measured.

## Test machine

| | |
|---|---|
| CPU | Intel Core Ultra 5 245KF |
| RAM | 16 GB DDR5-6000, one DIMM (single channel) |
| OS | Windows 11 Pro 26200 (10.0.26200) |
| Storage | NVMe SSD (Crucial CT1000E100SSD8), system-managed pagefile on C: |
| Memory manager | Memory compression on, page combining on (`Get-MMAgent`) |

All runs were made as a normal (non-admin) user on a desktop that was also being used for other work, so
system-wide counters contain noise from other processes (see "Noise and validity" below).

---

## Phase 0: hard cap on one idle app (13 Nov 2026)

Script: [`phase0-cap.ps1`](phase0-cap.ps1). It sets a hard working-set maximum (`SetProcessWorkingSetSizeEx`
with `QUOTA_LIMITS_HARDWS_MAX_ENABLE`) on one process, samples every 5 s, then restores the original limits.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File experiments\phase0-cap.ps1 -ProcessName WhatsApp.Root -Ratio 0.4 -CapSeconds 60
```

The English script was re-checked on a synthetic process (not a real app): WS 223 → 89 MB under the cap, and the
safe restore left it at 89 MB.

Raw data (written by the earlier Turkish version of the script, column names translated below):
`results/faz0_WhatsApp.Root_20261113_085728.csv`, `results/faz0_WhatsApp.Root_20261113_085927.csv`.
Columns: `Zaman` = time, `Asama` = phase (`once` before, `sinirli` capped, `sonrasi` after), `SistemBos_MB` =
`FreePhysicalMemory` (run 1), `Kullanilabilir_MB` = `AvailableMBytes` (run 2), `Sikistirma_MB` = working set of
the Memory Compression process. Numbers use a decimal comma.

**Run 1** — WhatsApp.Root, idle, hard max = 0.4 × WS = 102 MB, 60 s
- Working set 256 MB → 102 MB (−154 MB), flat for the whole cap.
- Page faults during the 60 s: ~155. Almost none of the trimmed pages were wanted back (they were cold).
- Memory Compression working set 190 → 228 MB (+38 MB), but only ~55 s after the cap (08:58:25), not at once.
  See B below: trimmed dirty pages wait on the modified list until the modified page writer runs.
- Private bytes unchanged (124 MB). Expected: commit does not drop, resident memory does.

**Bug found:** restoring the original *soft* maximum (~1.3 MB, the process default) made the kernel trim the
working set to 5 MB at once. A soft max is not enforced while memory is plentiful, but the call itself trims.
Fix: on restore use max = max(original, 2 × current WS). Confirmed by run 2 and by experiment C below.

**Run 2** — fix check, 20 s
- One minute after the accidental trim the WS was still 13 MB; it grew only to 16.4 MB.
  The idle hot set of WhatsApp.Root is ~13–16 MB, so ~94 % of the original 256 MB was not touched while idle.

**Measurement note:** `FreePhysicalMemory` excludes the standby list, where trimmed clean pages go.
From run 2 on, `AvailableMBytes` (free + zero + standby) is used.

## Phase 1: engine run, 12 min, WhatsApp (9 processes) (13 Nov 2026)

Raw data: `%LOCALAPPDATA%\RamPilot\olcum_20261113_091051.csv` (kept local: it lists local process names).
- Group WS 658 → 157–186 MB; private WS 361 → 72–90 MB. Hard faults while capped: 0
  (but see B: `HardFaultCount` cannot see compression-store reads, so this "0" does not prove no refaults).
- Controller steps: 81→73→66→59 MB and 79→71→64→58 MB, average refault 0.0/s.
- A CPU rise in the renderer (3302 Mcycles/s, incoming message) → uncap → re-probe. When the window got focus,
  three processes were uncapped in the same tick.
- Memory Compression +113 MB at the moment of trimming, later back to 565 MB. Available memory +250–380 MB.

## Engine's own memory: GC finding

- While the panel polled once per second, the engine grew to 22 MB private / 32 MB WS in 4 minutes.
  The `diag` command showed a live heap of 1.3 MB, only 2 gen0 GCs in 4 min and 18 MB committed: not a leak.
  The gen0 budget is derived from the L3 cache size, so it is very large.
- Fix: `IlcArg --runtimeopt:GCgen0size=400000` plus `GCConserveMemory=7`; the status JSON is built only on request.
- Result (same load, 4 min): committed 18 → 5.8 MB, private 22 → 10 MB, WS 32 → 20 MB (~10 MB of it shared DLLs).

### Conclusions so far (Phase 0/1)

The hypothesis holds: idle Electron/WebView2 apps keep a very large share of cold memory, and refaults after
trimming are very low. Open questions at that point: perceived latency when the user opens the window
(needs a human test), whether WebView2 child processes behave the same (process-tree targeting), and a
comparison with Discord / ChatGPT (Electron).

---

## Kernel-behaviour probes (A, B, C), 13 Nov 2026

Two design assumptions were unproven (code review 2, items marked *verify*):
the kernel evicts cold pages before hot ones under a hard working-set maximum (A), and RamPilot's
controller signal (`HardFaultCount`) sees refaults served from the compression store (B). C checks which
`SetProcessWorkingSetSizeEx` calls trim at once.

### Answers in short

- **A, victim selection: only partly true.** The first trim under a new hard max evicts in
  working-set-list order (about first-touched-first-out), not by access. It ignores which pages are hot,
  unless the process has been idle long enough for the working-set manager to age its pages.
  The trim kept exactly the same share of hot and cold pages (37.2–37.3 % each) after 30, 60 and 120 s idle
  (9/9 runs), and ≤ 0.3 % of the hot set when it was one contiguous block (9/9 runs). After 300 s idle it kept
  100 % of the hot set in 5 of 6 runs (A7, A7r) and 73.5 % in the sixth.
  *While capped*, replacement is access-aware in every run: the hot set was fully resident again within 1 s,
  and there were no more refaults of the test region for the rest of the step (up to 90 s).
  The price of a non-selective trim is a one-time refault burst about the size of the evicted hot set.
- **B, fault accounting: `HardFaultCount` does not include compression-store reads.** It was 0 in all
  9 store-backed measurements (884 736 refaulted pages). It counted only real file reads, one per 16-page
  read I/O. `PageFaultCount` rose by exactly 1.000 per refaulted page from every source. It also counts
  demand-zero faults of new allocations (1.002 per page), so it is not specific to refaults.
  Store reads are also invisible in the system's `Page Reads/sec` and `Pages Input/sec`.
- **C:** lowering a hard max trims at once. Raising or disabling it does not trim. A *soft* max below the WS
  trims at once (WS 305 → 10 MB), even though a soft max is otherwise not enforced: the next touch grew the
  WS straight back to 305 MB. This confirms the Phase 0 restore bug.

**What this means for RamPilot's controller**

1. `HardFaultCount` is blind to store reads, so it cannot show the cost of a trim. Phase 1 reported 0 hard
   faults, but that does not mean there were no refaults. Use the `PageFaultCount` delta while capped,
   minus the app's own uncapped baseline rate (demand-zero faults from new allocations), as the refault signal.
   Keep `HardFaultCount` as a separate "disk I/O" signal; it does fire for file-backed pages (DLLs, resources).
2. Do not rely on the kernel to pick victims at the moment a cap is applied, unless the process has been idle
   for minutes: aging was absent at 120 s (3/3 runs) and mostly complete at 300 s (5/6 runs fully selective, one
   73.5 %). Otherwise step down gradually: after the first step, each further 15 % step kept 94–100 % of the
   hot set (A3). A tiny "priming" trim does not help (A8).
3. Refaults are cheap and short-lived when the hot set fits under the cap. Measured costs per page:
   0.75–0.85 µs while the trimmed copy is still in RAM, 1.6–1.7 µs averaged once it must be decompressed
   from the store (≈ 14 µs per 16-page store read), and about 7 µs averaged for file-backed pages from NVMe
   (≈ 100 µs per 64 KB read). The burst after a non-selective 150 MB slam is about 7 700 faults
   (≈ 6–13 ms of CPU). After that, 0 faults.
4. A trimmed page does not free RAM at once. When memory is plentiful, trimmed dirty pages wait on the
   modified list, which is *not* part of "Available". With ~3 GB free, a 128 MB trim was still there 30 s later.
   They become available only when the modified page writer compresses them. This happened within 1–2 s
   of a 0.26–1.3 GB trim when free memory was ≤ 1.7 GB, or under pressure. A saved-memory figure must be
   measured, not inferred from the working-set drop.

### Method

Tool: [`kernel-probe/`](kernel-probe/), a stand-alone .NET 10 console app (not part of `RamPilot.sln`).
A controller process starts a synthetic **worker** child, applies limits to it with
`SetProcessWorkingSetSizeEx`, and measures it:
- per-page residency: `QueryWorkingSetEx` (`PSAPI_WORKING_SET_EX_BLOCK.Valid`) for every page of the test region;
- per-process counters: `NtQuerySystemInformation(SystemProcessInformation)` x64 offsets
  `WorkingSetPrivateSize @8`, `HardFaultCount @16`, `CycleTime @24`, `KernelTime @48`, `PageFaultCount @128`,
  `WorkingSet @144`. The same walk gives the `Memory Compression` (store) process;
- system counters: PDH raw values of `\Memory\Transition Faults/sec`, `Demand Zero Faults/sec`, `Page Reads/sec`,
  `Pages Input/sec`, `Modified Page List Bytes`, the three `Standby Cache * Bytes` counters, and
  `Free & Zero Page List Bytes`;
- per-page latency, measured in the worker: each page access is bracketed by `QueryPerformanceCounter`
  (100 ns resolution) and counted in a histogram (<0.3, 0.3–1, 1–2, 2–4, 4–8, 8–32, ≥32 µs).

Worker memory: `VirtualAlloc` private pages, each with unique content (1 KB pseudo-random + 3 KB per-page
constant), so page combining cannot merge them and they compress about 3–4×. Alternatives: a read-only
mapped file (clean pages) or a pagefile-backed section. Only the worker and a short-lived `pressure`
child (B only) were ever capped or allocated. No real application was touched. Children run in a
kill-on-close job object. All processes exited at the end and `kernel-probe/tmp` was empty.

**Reproduce everything** (about 90 min, non-admin, needs the .NET 10 SDK):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File experiments\kernel-probe\run-all.ps1
```

or individual commands from [`kernel-probe/run-all.ps1`](kernel-probe/run-all.ps1), e.g.
`experiments\kernel-probe\bin\Release\net10.0-windows\kernel-probe.exe victim --runs 3 --caps 150 --step-s 60 --warmup-s 30 --tag A1_slam150`.
Output goes to `results/kernel-probe/` (`A_*.csv`, `B_*.csv`, `C_*.csv`, and `console_YYYYMMDD.txt` = the console log).
The A7r repeat is the A7 line with `--tag A7r_idle300s_repeat`.

### Noise and validity

- The desktop was in use. Another agent ran the RamPilot engine on WhatsApp, and a benchmark agent ran a one-shot
  `EmptyWorkingSet` pass over 16 children of `powershell.exe` (~1.2 GB) during this session. The suite runs
  (C, A1–A7, B) were children of `powershell.exe` (`run-all.ps1`); the follow-ups (A7a, A7b, A7r, A8, A9, A10)
  were children of `bash`. To catch an external trim, every run has a guard, written to the `guard` column:
  - A: the region must be 100 % resident before the cap; under a cap the WS must stay ≥ 85 % of the cap; region
    residency must not fall by > 5 % of the cap between samples of one step, nor after uncapping;
  - B: never-trimmed regions must be 100 % resident before they are touched or trimmed;
  - C: the WS must not fall between steps, nor in the 2 s after a call.

  **No guard tripped in any reported run** (every row `ok`). Exploratory runs made before the guards existed
  were deleted and are not reported.
- System counters (PDH) are system-wide, so they include other processes. Per-process counters
  (`PageFaultCount`, `HardFaultCount`) and `QueryWorkingSetEx` are exact for the worker.
- B is intrusive. To push the worker's standby copies out it allocates
  free + low-priority standby + modified + 512 MB (2.7–3.6 GB) for 3 s. This repurposed other processes'
  low-priority standby cache, and once (B run 3) the store's own working set was trimmed (577 → 88 MB).
  Exploratory B runs earlier in the day pushed the store from 765 → 118 MB in the same way.
- The worker crashed with an `AccessViolationException` at exit in some of the suite runs: the hot thread
  touched the region after it had been freed. This happened after the last sample of each run, so no
  measurement is affected. It was fixed (the hot thread is joined before unmapping) before the follow-up runs.
- Memory compression and its thresholds depend on machine state. Free memory ranged from 0.2 to 3.7 GB
  during the runs. The 300 s result (A7) is the one most likely to depend on machine state, which is why
  it was repeated later (A7r).

### A. Victim selection under a hard working-set maximum

Setup: 400 MB private region, fully written. "Hot" = 40 MB (10 240 pages) read every 100 ms by a worker
thread. The layout is either `stride` (every 10th page, interleaved with cold pages) or `block` (one
contiguous 40 MB block in the middle). The worker idles for `warmup` seconds before the cap. Its .NET
runtime adds ~23 MB to the WS (423 MB in total). Hot touching is paused for the few ms around the
`SetProcessWorkingSetSizeEx` call, so the "immediate" sample shows only the kernel's choice; it then resumes.
Residency is sampled at 0, 1, 2, 5, 10, 20, 30, 45, 60, 90 s.

Results, 3 runs each (per-run values; all guards ok). "Random/FIFO" expectation for cap 150 MB:
hot ≈ cold ≈ 37 %; perfect LRU: hot 100 %.

| Variant | Cap(s) MB | Idle before cap | Hot kept at trim | Cold kept at trim | Hot after 1 s → end | Region faults in step | Faults after +5 s |
|---|---|---|---|---|---|---|---|
| A1 stride, 100 ms | 150 | 30 s | 37.3 / 37.2 / 37.3 % | 37.3 / 37.2 / 37.3 % | 100 % | 7752 / 7711 / 7755 | 0 / 0 / 0 |
| A7a stride | 150 | 60 s | 37.2 / 37.2 / 37.2 % | 37.2 / 37.3 / 37.2 % | 100 % | 7696 / 7631 / 7716 | 0 / 0 / 0 |
| A7b stride | 150 | 120 s | 37.3 / 37.2 / 37.2 % | 37.3 / 37.3 / 37.2 % | 100 % | 7738 / 7715 / 7718 | 0 / 0 / 0 |
| **A7 stride** | 150 | **300 s** | **100 / 100 / 100 %** | 29.2 / 24.0 / 24.0 % | 100 % | 425 / 0 / 0 | 0 / 0 / 0 |
| **A7r** repeat of A7, ~45 min later | 150 | 300 s | **100 / 73.5 / 100 %** | 24.3 / 33.0 / 24.3 % | 100 % | 3 / 4429 / 1 | 0 / 0 / 0 |
| A4 block | 150 | 30 s | **0.0 / 0.0 / 0.0 %** | 41.3 / 41.4 / 41.4 % | 100 % | 10 787 / 10 783 / 10 809 | 0 / 0 / 0 |
| A9 block (rerun with address profile) | 150 | 30 s | 0.0 / 0.0 / 0.0 % | 41.4 % (3/3) | 100 % | 10 698 / 10 806 / 10 720 | 0 / 0 / 0 |
| A10 block, reverse fill order | 150 | 30 s | 0.3 / 0.0 / 0.3 % | 39.5 % (3/3) | 100 % | 10 524 / 10 493 / 10 537 | 0 / 0 / 0 |
| A5 stride, clean file-backed pages | 150 | 30 s | 42.5 / 39.6 / 42.0 % | 33.6 / 34.5 / 33.5 % | 100 % | 6841 / 7217 / 7752 | 0 / 0 / 0 |
| A6 stride, hot touched every 2 s | 150 | 30 s | 37.3 % (3/3) | 37.3 % (3/3) | 100 % | 8372 / 8388 / 8484 | 744 / 747 / 766 ¹ |
| A2 gentle step, then target | 300 → 150 | 30 s | 74.7 % (3/3) → **77.3 / 78.3 / 79.0 %** | 74.6–74.7 % → 32.1–32.2 % | 100 % | 3476 / 3400 / 3397, then 3375 / 3250 / 3225 | 0 |
| A3 descent ×0.85 every 15 s | 340, 290, 245, 210, 180, 150 | 30 s | 84.4–84.7 % at 340; **94.3–100 %** at every later step | follows the cap | 100 % | 2237–2319 at 340, then 315–791 per step (4 985 / 5 021 / 4 964 total) | 0 ² |
| A8 2 % "prime", then target | 415 → 150 | 30 s | 100 % (3/3) → **37.7–38.2 %** | 97.8–98.0 % → 37.2 % | 100 % | 0–2, then 8431 / 8434 / 8476 | 0 |

¹ A6: the late faults are the worker's .NET runtime faulting its own pages in at ~55–60 s. Region residency fell
by the same 2.8 MB that "other" WS grew; the hot set stayed at 100 %.
² One sample in A3 run 1 had 22 faults after +5 s.

Where the first trim cuts (resident % per tenth of the region, lowest address first; column
`res_by_address_decile_pct`, recorded from A8 on):

| Variant | Right after the cap |
|---|---|
| A9 block, forward fill (3 runs identical) | `0 0 0 0 0 0 72 100 100 100` |
| A10 block, reverse fill (3 runs) | `8–14 100 100 100 41–48 0 0 0 0 0` |
| A7a / A7b stride, 60 s / 120 s idle (6 runs) | `0 0 0 0 0 0 72–73 100 100 100` |
| A8 stride, 2 % prime at 415 MB (3 runs) | `80–82 100 100 100 100 100 100 100 100 100` (no hot page evicted) |
| A8 then 150 MB (3 runs) | `0–1 0 0 0 0 0 72 100 100 100` |

Reading the evidence:
- With forward fill the oldest (first-touched) low addresses go first; with reverse fill the high addresses go
  first. So the trim follows the order in which pages entered the working set, not addresses and not accesses.
  With an interleaved hot set this gives the identical hot/cold ratio of A1 (37.3 % / 37.3 %). A hot block that
  happens to be old is evicted entirely (A4/A9/A10: 0 %).
- The working-set manager does age pages without any memory pressure, but slowly. A small trim already skipped
  the hot pages at 30 s (A8 prime: no hot page evicted, 2 % of cold pages evicted). A large trim was still FIFO
  at 120 s and fully selective at 300 s in 5 of 6 runs (one run kept 73.5 %, with a different address pattern).
  The exact mechanism (aging-scan rate, accessed-bit handling) is inferred
  from these results; it is not documented by Microsoft.
- Once a trim pass has run, later trims are selective (A2 second step, A3). With a big second step
  (300 → 150, A2), 77–79 % of the hot set was kept; with small steps (A3), 94–100 %. A3's total refaults
  (~5 000) were about two thirds of a single slam (~7 700).
- Steady state under the cap is LRU-like in every variant: each hot refault replaces a non-hot page. Within
  1 s the hot set is 100 % resident and stays so, and region faults stop (0 after +5 s in all runs, apart from
  the runtime noise noted above). The "faults in step" column ≈ hot pages lost at the trim + some runtime pages.
- Clean file-backed pages (A5) behave the same. The slight hot bias there (40–42 % vs 34 %) is not explained.

### B. Fault accounting for trimmed pages

Setup: worker with memory priority 1 (`SetProcessInformation(ProcessMemoryPriority)`), so its trimmed pages
land in low-priority standby and are repurposed first. Trims use `VirtualUnlock` on the exact range (documented
to remove unlocked pages from the working set), except S7, which uses RamPilot's method (hard max 8 MB, then
release). Before each measured touch the worker touches a 1 MB resident "warm" region twice, so code-path faults
are not counted. A touch = read 1 byte per page, sequentially.

States (128 MB = 32 768 pages; S4 and S7 512 MB = 131 072 pages):
- S0 never trimmed; S1 first write to a fresh allocation (demand-zero);
- S2 trimmed and touched at once (modified list);
- S3 trimmed, 30 s idle, no pressure;
- S3b and S4–S7 trimmed together (~1.3 GB, enough to wake the modified page writer), 15 s idle.
  Then S3b is touched (compressed, standby copy still in RAM). Then the `pressure` child allocates
  free + low-priority standby + modified + 512 MB for 3 s, which repurposes the standby copies.
  After that S4 (private) is touched, from the store; S5 (read-only mapped file), from disk;
  S6 (pagefile-backed section); S7 (private, trimmed via hard cap).

Command: `kernel-probe.exe faults --runs 3 --wait-s 30` → `B_faults_20261113_112727.csv`, `B_lists_20261113_112727.csv`.
Results, median (min–max) of 3 runs. `PF`/`HF` are the worker's own `PageFaultCount`/`HardFaultCount`
deltas:

| State | µs / page | PF / page | HF (per run) | Pages ≥ 8 µs | System Page Reads (per run) | Store CPU (Mcycles) |
|---|---|---|---|---|---|---|
| S0 resident | 0.06 (QPC overhead) | 0.0001 | 0, 0, 0 | 0 % | 0, 0, 0 | 0 |
| S1 demand-zero write | 0.41 (0.41–0.41) | 1.002 | 0, 0, 0 | 0 % | 0, 0, 0 | 0 |
| S2 trimmed, touched at once | 0.79 (0.75–1.09) | 1.000 | 0, 0, 0 | 0 % | 0, 0, 0 | 0 |
| S3 trimmed + 30 s idle | 0.81 (0.79–0.88) | 1.000 | 0, 0, 0 | 0 % | 0, 0, 0 | 0–109 |
| S3b compressed, standby copy alive | 0.85 (0.82–0.94) | 1.001 | 0, 0, 0 | 0 % | 0, 0, 20 ³ | 31–33 |
| **S4 private, from store** | **1.64 (1.57–1.74)** | **1.000** | **0, 0, 0** | **6.3 % (= 1/16)** | 0, 1, 18 ⁴ | 169–183 |
| S5 file-backed, from disk | 7.14 (6.16–7.21) | 1.000 | **2048, 2048, 2048** | 6.3 % (all ≥ 32 µs) | 2048, 2048, 2048 | 0 |
| S6 pagefile section, from store | 1.69 (1.63–2.16) | 1.000 | 0, 0, 0 | 6.3 % | 0, 0, 0 | 54–57 |
| S7 private, hard-cap trimmed, from store | 1.72 (1.61–1.95) | 1.000 | 0, 0, 0 | 5.6–6.3 % | 0, 0, 0 | 659–757 ⁵ |

³ System-wide: another process.
⁴ In run 3 the pressure had also trimmed the store process (577 → 88 MB). During the S4 touch the store took
32 969 faults, 18 of them hard (real pagefile reads), all charged to the Memory Compression process; worker HF
stayed 0.
⁵ S7 also showed a reproducible −133 to −135 MB drop of the store WS during its touch. That is unexplained.

How a store read looks: in S4, about 1 page in 16 took 8–32 µs and nearly all others took < 1 µs.
The kernel reads a 16-page cluster from the store (decompression), and the remaining 15 pages are then mapped
by cheap transition faults. For S4, `PF − system Transition Faults` was 8394 and 8314 (runs 1–2; run 3 too
noisy), which ≈ 8192 = 131 072 / 16. So the cluster-leading store reads are counted neither as transition
faults nor as page reads; they appear only in total page faults. Disk reads (S5) cluster the same way
(2048 reads × 16 pages), but each costs ~100 µs instead of ~14 µs. Pagefile-backed sections (S6) also go
through the store.

Lifecycle of trimmed dirty pages (`B_lists_20261113_112727.csv`, `B_watch_20261113_112605.csv`, `B_watch_20261113_112706.csv`):
- With ~3 GB free (B runs 2–3, S3), a 128 MB trim sat on the modified list for the full 30 s (modified
  ~25 → ~150 MB, store unchanged, Available only +23 to +96 MB).
- A 256 MB trim with 1.7 GB free and an 800 MB trim with 0.9 GB free (`kernel-probe watch`) were compressed
  within 1–2 s: for 800 MB, modified 833 → 69 MB, store +201 MB (≈ 4× compression), low-priority standby +752 MB.
- After compression the original physical pages stay on standby. A touch then costs a soft fault (S3b,
  0.85 µs), and the store keeps a duplicate until the standby page is repurposed.
- Batch trim of ~1.3 GB (B runs 2–3): Available +120 to +124 MB just after the trim, +909 to +1 221 MB 15 s later.

No real pagefile read of a private page was charged to the worker. The only pagefile reads seen were the
store's own (S4 run 3), and they were not charged to the faulting app.

### C. Which calls trim at once

`kernel-probe.exe trim --runs 3`, 300 MB region (WS ≈ 323 MB). All 3 runs agree within 2 MB
(`C_trim_20261113_104019.csv`):

| Call | WS before → right after (MB) | Trims? |
|---|---|---|
| hard max 150 MB (`MIN_DISABLE \| MAX_ENABLE`) | 323 → 150.0 | yes, inside the call (2–30 ms in A) |
| raise hard max to 1024 MB | 150 → 150 | no |
| touch region under 1024 MB hard max | 152 → 305 | grows normally |
| disable hard max (`MAX_DISABLE`, max 1024 MB) | 305 → 305 | no |
| **soft max 10 MB** (`MAX_DISABLE`) | 305 → **10.1** | **yes, at once** |
| touch region under 10 MB soft max | 10 → 305 | soft max not enforced afterwards |
| soft max = 0.5 × WS | 305 → 152 | yes, to the new soft max |
| soft max = 2 × WS | 302 → 302 | no |
| hard max = WS + 64 MB | 302 → 302 | no |

So any `SetProcessWorkingSetSizeEx` whose max is below the current WS trims to it immediately, soft or hard.
When restoring, never write a max below the current WS (Phase 0 fix confirmed).

### Raw data index (`results/kernel-probe/`)

| File | Content |
|---|---|
| `A_<tag>_<time>.csv` | A time series, one row per sample: run, label, t, cap, WS, private WS, region resident MB, hot %, cold %, other WS, `PageFaultCount`, `HardFaultCount`, store WS, call ms, guard, residency per address tenth (A8 on) |
| `B_faults_<time>.csv` | B: one row per run × state, all counter deltas, latency histogram, guard |
| `B_lists_<time>.csv` | B: modified / standby (by priority band) / free / available / store WS at each phase |
| `B_watch_<time>.csv` | Trimmed-page lifecycle, sampled each second |
| `C_trim_<time>.csv` | C: WS before / right after / 2 s after each call, resulting limits, guard |
| `console_<date>.txt` | Console log of every run |
