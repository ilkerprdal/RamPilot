# Fable review 2 — codebase and repo (2026-11-13)

Static read of all engine/panel C#, docs and scripts; nothing was built or run. Items marked *verify* are unconfirmed assumptions.

## 1. Correctness and safety (by severity)

**S1 — can hurt the user**
- **Exclusion fails open.** `TrackedProcess.cs:75-78`: 8 KB buffer (~4,090 chars); a longer Chromium command line returns `""`, so role becomes `main` and `Excluded=false` (`:55`). GPU/audio/crashpad processes then get capped. Retry with the returned length; if still unknown, exclude.
- **Probe is a one-shot trim to 32 MB.** `Engine.cs:206` does `SetCap(MinCap)` then `Uncap()`. That is `EmptyWorkingSet` with a floor, and it contradicts `README.md:6`. It repeats after every CPU blip (`Engine.cs:191-196`, confirmed in NOTLAR Faz 1), so a chatty app is fully trimmed on a loop.
- **Hard kill leaks caps.** Recovery needs `capped.txt` and a relaunch (`StateStore.cs:25-41`). Task Manager kill, native fail-fast or uninstall leaves hard limits until the target exits. The file is written after the cap is applied (`Engine.cs:177-181`), not before.
- **Audio apps in the default list without audio detection.** `Config.cs:11-12` includes Spotify and Discord; only the 4%-of-a-core CPU heuristic protects playback or a call.

**S2 — wrong behaviour**
- **Event wakes resample with tiny dt.** `Program.cs:115` wakes on focus/IPC; `Engine.cs:129` allows dt = 0.05 s. A 10 ms CPU burst then reads as "active" (uncap, later re-probe), and 3 faults read as 60/s (cap ×1.75, `:238-239`). Measure on a fixed cadence; event wakes should only handle focus and inbox.
- **I/O errors kill the engine.** `_csv.WriteLine` (`Engine.cs:170`) and `StateStore.Save` (`:179`) throw out of `Tick`; the loop exits with code 0 and the tray icon vanishes.
- **Cleanup races Tick.** Signal and `UnhandledException` handlers call `engine.Dispose()` from another thread (`Program.cs:85-93`) while `Tick` iterates `_procs`. A throw inside `Dispose` leaves `cleanupDone=1`, so the `finally` cleanup is a no-op.
- **Focus protection has holes.**
  - `SetWinEventHook` result is unchecked (`ForegroundWatcher.cs:34`); on failure the foreground PID is frozen at startup.
  - No per-tick `GetForegroundWindow` poll to heal a missed event.
  - UWP windows owned by `ApplicationFrameHost.exe` never resolve to a target root.
  - A tracked process keeps its original root PID (`Engine.cs:149`); if the root is replaced, focus matching fails.
- **Decrease uses 60 s of data, not 300.** The first step-down happens 60 s after capping (`Engine.cs:250-253`). There is no memory of past failures, so periodic GC produces an endless sawtooth: decay to 32 MB, spike, ×1.75, decay.

**S3 — hardening**
- **Pipe name is machine-global** (`IpcServer.cs:10`). A second logged-in user gets access-denied and logs an error every second. Use a per-session name.
- **Client does not verify the server.** `PipeLink.cs:56` and `build.ps1` omit `CurrentUserOnly` and default to impersonation-level tokens. Add `CurrentUserOnly` and `TokenImpersonationLevel.Identification`.
- **Server is unbounded.** `ReadLine` has no length limit (`IpcServer.cs:56`) and there is one thread per connection (`:32-36`). Cap line length and instances. Remove the hidden `diag` command (`Engine.cs:83-95`).
- **Struct offsets** (`ProcessTable.cs:28-30`) match the public x64 layout; I checked each one. But there is no bounds check in the walk and no startup self-check. A failed query returns an empty table (`:42`), which uncaps and then re-probes everything.
- **Sleep/resume:** `TickCount64` counts suspended time, so the first tick after resume has a huge dt. Add a post-resume grace period.
- **Bridge is sound:** allowlisted commands rebuilt field by field (`MainForm.cs:144-154`), CSP present, UI uses `textContent` only, navigation locked. `setApp` is validated against the allowlist engine-side.
- `IlcGenerateStackTraceData=false` (`RamPilot.csproj:17`) makes user crash reports useless.
- Per-tick allocations: a string per system process (`ProcessTable.cs:50`), `uint.Parse` in `RootPid` (`TrackedProcess.cs:35`).

## 2. Controller versus state of the art
- **Signal is count-based, not cost-based.** TMO/senpai targets stall time (PSI). Build an estimated stall budget: soft faults × ~µs, store faults × ~tens of µs, disk faults × ms, with a target such as <0.1% of wall time. Absolute 5/60 faults/s ignores cap size.
- **`HardFaultCount` may be blind to compression-store reads** (*verify*). Faz 1 reported 0 hard faults while the store grew 113 MB. Test: trim, wait, touch, read the delta.
- **Victim selection is assumed, not measured** (*verify*). The design rests on the kernel evicting by age under a hard max. Prove it with a synthetic hot/cold process and `QueryWorkingSetEx`.
- **Replace the slam with a gradual descent:** start at 0.8×WS, step ×0.85 every 30 s while cost is under budget. No hot pages are evicted by design.
- **Remember what was learned:** per (exe, role) last-good cap and an ssthresh-style floor after each spike, persisted. Re-entry after activity reapplies the known cap with no re-probe.
- **Demand-aware governor (the biggest gap versus TMO).** With 8 GB free, compressing idle pages costs CPU and buys nothing. Scale aggressiveness by memory load, commit headroom, `CreateMemoryResourceNotification`, fullscreen/game state and battery. Back off globally when pagefile writes rise.
- **`savedMb` is not honest yet** (`Engine.cs:318-319`, tooltip `:329`):
  - it is gross: store growth is not subtracted (Faz 0: 154 MB trimmed, 38 MB store);
  - the baseline is frozen at entry, so the app's own shrinkage is credited to RamPilot;
  - it peaks during the probe, right after the full trim.
  - Fix: report gross trimmed, estimated store cost and net separately. Add a rotating holdout app for a real counterfactual.

## 3. Being provably better
- **Capabilities to add:**
  - audio-session awareness (`IAudioSessionManager2`);
  - predictive unlock on Alt-Tab start (`EVENT_SYSTEM_SWITCHSTART`) and taskbar hover;
  - a guardian process, so no cap ever outlives the engine;
  - learned per-app profiles;
  - a published latency objective;
  - opt-in commit reduction via `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`.
- **Benchmark (`bench/`), three arms:** no tool; RamPilot; an `EmptyWorkingSet` loop every N minutes. Fixed app versions, reboot, 10 min warm-up, 30 min idle, randomized order, at least 10 runs.
- **Focus latency:** synthetic Alt-Tab plus keystroke via `SendInput`; time to first changed frame (`Windows.Graphics.Capture`, or PresentMon/ETW; admin is acceptable for the benchmark only). Also faults and hard faults in the first 5 s.
- **Pressure scenario:** a memory hog allocates until available RAM is low; measure its allocation time and foreground hard faults. This tests the "proactive beats reactive" claim.
- **Publish:** available MB, private WS, store size, pagefile writes, CPU-seconds of the engine and of Memory Compression; medians and p95 with confidence intervals; raw CSVs, scripts, machine spec, and negative results.

## 4. Open-source readiness
- **Not a repo yet:** no `.git`, `.gitignore`, LICENSE or `.sln`. `dist/` binaries and PDBs embed `C:\Users\<name>` paths; never commit them. Use `ContinuousIntegrationBuild` and `PathMap`. Text sources are clean of personal paths.
- **Layout:**
  - `src/RamPilot.Core` (pure controller, no P/Invoke), `src/RamPilot.Engine`, `src/RamPilot.Panel`
  - `tests/`, `bench/`, `experiments/` (was `deney/`), `docs/`
  - `Directory.Build.props`, `global.json`, `.editorconfig`
- **Language:** English identifiers, comments, logs and CLI flags (`--dakika`, `--kurtar`, `--ozet`). English README plus `README.tr.md`. The engine should emit structured events and the panel should localize them; today Turkish strings travel over IPC.
- **Stale docs:** `TASARIM.md:52` says .NET 8; `README.md:4` says "system service"; `README.md:36` lists Faz 4 as not done.
- **`build.ps1:5-6`** hard-codes a per-user dotnet path and the VS installer path; use `dotnet` from PATH.
- **License:** MIT gives the widest adoption. Apache-2.0 adds a patent grant. GPL-3.0 blocks closed "RAM booster" rebrands, which is a real risk in this niche, at the cost of reuse. I would pick Apache-2.0, or GPL-3.0 if rebranding matters most to you.
- **CI:** `windows-latest`, AOT publish, tests, `dotnet format --verify-no-changes`, exe size budget. Tag-driven releases with SHA-256 and build attestation.
- **Signing:** an unsigned AOT binary that opens other processes and writes a Run key will trip SmartScreen and antivirus. Plan for SignPath Foundation or Azure Trusted Signing, plus a winget manifest.
- **Tests:**
  - controller as a pure `Decide(state, sample, config)` against a simulated process (hot set plus GC spikes), with invariants: never cap focused, never below floor, always uncap on exit;
  - offset test comparing own entry with `GetProcessMemoryInfo`/`GetProcessTimes`;
  - `FindAppRoot` with synthetic PID reuse;
  - integration: cap a dummy child, `TerminateProcess` the engine, assert the cap is cleared.
- **Config:** `config.json` with clamped values, a user-editable allowlist, and a hard deny list (games, anti-cheat, security software) that users cannot override.
- **Community files:** SECURITY.md (threat model, private advisories), CONTRIBUTING, CHANGELOG, SemVer, IPC version check in the panel.
- **Privacy:** logs and CSVs hold process names and PIDs, local only. State that the engine makes no network calls and document WebView2's own behaviour.

## 5. Verdict
The mechanism is real and the code is small and readable. But today's policy is a cleaner with a feedback loop bolted on: the probe trims everything, activity re-triggers it, and the headline number is gross. Fix those and prove victim selection, and the "not a RAM cleaner" claim holds.

**P0 — before publishing**
1. Fail-closed exclusion with a growable command-line buffer.
2. Replace the slam probe with gradual descent; persist learned caps; no re-probe after activity.
3. Guardian process plus a startup scan that clears any allowlisted process with `HARDWS_MAX_ENABLE` set; write the journal before capping.
4. Fixed-cadence sampling; `Tick` survives I/O errors; cleanup marshalled to the main thread.
5. Audio-session detection, or drop Spotify/Discord from the defaults.
6. Honest `savedMb` (gross / store cost / net).
7. Repo hygiene: git, `.gitignore`, LICENSE, no `dist/`, English README, portable `build.ps1`.

**P1**
- Focus hardening (hook check, poll, `ApplicationFrameHost`, root refresh).
- Startup offset self-check and bounds checks.
- Per-session pipe name, client `CurrentUserOnly`, bounded server.
- `Core` library with tests, CI, stack traces enabled.
- Verify the store-fault and victim-selection assumptions.
- Benchmark harness and first published results.

**P2**
- Demand-aware governor and stall-budget controller.
- Config file, deny list, localization.
- Signing and winget.
- ARM64, resume grace, allocation cleanup.
