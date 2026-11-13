# Final pre-publish review 3 (2026-11-13)

Read all engine and core code, safety, IPC, docs, experiments, CI. Ran locally: build (0 warnings), tests (81/81 pass), `dotnet format --verify-no-changes` (**fails**). Nothing was modified. The engine was not run live.

## Verdict: not yet

The engineering is in good shape: every P0 from review 2 is fixed in code. What blocks publishing is a red CI on day one, no live run of the shipped policy, and four README claims the code or data does not support.

## 1. Review 2 items

| Item | Status | Evidence |
|---|---|---|
| P0-1 fail-closed exclusion | Fixed | `ProcessHandle.cs:46-72` (growable, null on failure); `RoleClassifier.cs:26-27` |
| P0-2 no slam, persisted caps, no re-probe | Fixed | `Controller.cs:253-288` (first cap = max(0.9 × WS, limit)); `LearnedProfiles.cs`; restore path `:261-275` |
| P0-3 guardian, startup scan, journal first | Fixed | `Guardian.cs:70-103`; `Engine.cs:92-99`; journal `:433` before `SetHardMax` `:439` |
| P0-4 fixed cadence, tick survives errors, cleanup on main thread | Fixed | `Program.cs:179-188`, `:148-167`, `:191-198`; `Engine.cs:227-240` |
| P0-5 audio detection | Fixed | `AudioSessions.cs`; `Controller.cs:148-153`. Vtable order and IIDs are correct. |
| P0-6 honest savings | Fixed | `Savings.cs:19-36` (commit-scaled counterfactual, store ratio ≥ 0.25) |
| P0-7 repo hygiene | Partially | LICENSE, `.gitignore`, English README, portable `build.ps1` present; CI fails (§4) |
| P1 focus hardening | Fixed | `FocusTracker.cs:37-43`, `:57-69`; root refresh `Engine.cs:349-350` |
| P1 offset self-check, bounds | Fixed | `ProcessSampler.cs:72-113`, `:142-168`; `Program.cs:116-122` |
| P1 pipe hardening | Fixed | `IpcServer.cs:15-16`, `:31-38`, `:103`; `PipeLink.cs:67-70` |
| P1 Core library, tests, stack traces | Fixed | 81 tests pass; `RamPilot.Engine.csproj:22` |
| P1 verify kernel assumptions | Fixed | `experiments/README.md` probes A/B/C, raw CSVs present |
| P1 benchmark harness and results | Partially | Harness exists; never run (README says so) |

## 2. New issues from the rework

**Guardian**
- It cannot cap; it only clears. If it cannot open the engine it scans once and exits; the engine then sees it dead and blocks tightening (`Engine.cs:279`, `:428`). No fight loop.
- Existing `Holding` caps stay in place while the guardian is down. If it cannot be restarted (exe removed, antivirus), they persist unguarded. Release everything after N failed restarts.
- An old guardian can clear a new engine's caps after a fast restart. In practice the 300 s first-cap rule makes this window empty.

**Startup scan**
- It clears `HARDWS_MAX` on every allowlisted-tree process, including limits set by another tool or the app itself (`Guardian.cs:34-47`). That is acceptable for an allowlist, but `DESIGN.md` §5.6 should say so.
- `Reapply` re-caps every tick with no back-off when something keeps clearing the limit (`Controller.cs:175`). It also re-applies the old cap after the WS regrew, which is one large trim that bypasses the 10 % rule.

**Release path**
- If `ClearHardMax` fails, the journal entry is still dropped and the state becomes `Free` (`Engine.cs:414-416`, `:401-402`). The cap leaks until the next scan. Keep the entry and retry.

**Idle-baseline subtraction**
- The baseline is updated during focus grace and activity hold, not only when idle (`Controller.cs:164` runs before the checks at `:165-166`). `DESIGN.md` §3 says "uncapped and idle".
- It is frozen while capped. A stale baseline of up to 500 faults/s hides 15 ms/s of budgeted cost, 10–30 times the budget. Decay it while capped, or bound it relative to the budget.

**Governor**
- Nothing raises or releases a cap when pressure falls; the depth limit only stops further descent.
- A restored profile uses the stored reference WS (`Controller.cs:265`). If the app has grown since, the depth limit is far below 60 % of the current WS, so the low-pressure bound is bypassed. Cost checks still apply.

**Re-entry**
- After activity, a capped-before process is re-capped after 30–60 s with a 10 % step. `DESIGN.md` §3 justifies this by earlier aging, but no probe measured a re-cap after release and regrowth. List it as assumed in §8.

**Learned profiles, IPC, audio**
- A corrupt `learned.json` yields an empty store (`LearnedProfiles.cs:84-85`); implausible values are dropped; a hostile tiny cap is bounded by `minCapMb`. Fine.
- IPC bounds are correct. A client that stops reading pins one of four slots (no write timeout, `IpcServer.cs:95`); same-user only.
- COM wrappers are released by the GC, not explicitly, on a process that rarely collects. Check native handle growth over a day.

## 3. Docs versus code and data

- **`README.md:139-143` results table.** 361 → 72–90 MB comes from the old slam controller, and its raw CSV is not in the repo (`experiments/README.md` says "kept local"). The only live run of the gradual controller at low pressure reported net 7–27 MB (`tests/live/evidence-2026-11-13.txt:53-55`). The "Hard faults: 0" row is contradicted by probe B, which shows that counter cannot see store reads.
- **`README.md:104-105`** says low memory pressure releases limits. No code path does this: `ReleaseReason.PressureLow` is never produced. `docs/IPC.md:89` lists the same dead reason.
- **`README.md:106`** "No limit outlives the engine" is absolute. `DESIGN.md` §7 admits "End process tree" kills the guardian too.
- **`README.md:239-240`** says the governor "stays mostly idle" on a 32 GB machine. Default low aggressiveness is 0.4, so caps still descend to 60 % of the working set (`EngineConfig.cs:230`; `DESIGN.md:166`).
- **`README.md:55`** "Hot pages kept in measurements" rests on a synthetic worker; 5 of 6 runs at 300 s. Say "synthetic".
- **`README.md:100`** "never runs elevated": it does not request elevation, but nothing stops a user from starting it elevated.
- **The shipped defaults have never been observed capping a real app.** After the final tuning the only live run was 90 s, in which no cap was applied (`evidence-2026-11-13.txt:96-99`). Everything else is unit tests and synthetic probes.

## 4. OSS readiness

- **CI will be red.** The format step exits 2: 41 `WHITESPACE` errors in `Engine.cs`, `Tray.cs` and `SimulatedProcess.cs`. Separately there are 3,027 `ENDOFLINE` errors locally because files are LF, `.editorconfig` demands CRLF and there is no `.gitattributes`; whether those appear on the runner depends on checkout settings.
- **Placeholders:** `README.md:173` has `<owner>/RamPilot`. `CODE_OF_CONDUCT.md` names no contact, only "the address on the maintainers' profiles".
- **Licensing:** the release zip ships WebView2 SDK DLLs without their licence notice. Add `THIRD-PARTY-NOTICES` to `release.yml` before the first release.
- No commits yet; no personal paths in tracked text; `artifacts/` and PDBs are ignored. Engine exe is 3.6 MB against an 8 MB budget.
- `experiments/README.md` mentions "another agent" and "a benchmark agent" running during the probes. Honest, but decide whether you want that wording public.

## 5. Lists

**Blocking**
1. Make CI green: fix the 41 whitespace errors, add `.gitattributes`, confirm the format step passes on a clean checkout.
2. Run the final binary on a real app for at least 30–60 minutes; commit the log excerpt and CSV.
3. Fix the README: replace the results table with numbers from that run (or label the old ones as the retired controller and drop the hard-fault row); remove "low memory pressure" from the release list and `IPC.md`; soften "No limit outlives the engine"; correct the 32 GB answer.
4. Fill `<owner>` and give the Code of Conduct a real contact channel.

**Non-blocking**
- Baseline: update only when idle, decay while capped.
- Keep the journal entry when a release fails; back off `Reapply`.
- Release all caps if the guardian stays down.
- Refresh the reference WS on restore.
- Mark re-entry selectivity as assumed, or probe it.
- State in `DESIGN.md` that the scan clears foreign limits.
- `THIRD-PARTY-NOTICES`; explicit COM release; pipe write timeout.
- Run the benchmark.
