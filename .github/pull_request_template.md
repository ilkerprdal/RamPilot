## What and why

<!-- What does this change, and which problem does it solve? Link the issue: "Fixes #123". -->

## How it was tested

<!-- Unit tests, manual steps, machine (Windows build, RAM). -->

## Measurements (controller, engine or panel performance changes)

<!-- Before/after numbers from bench/ or the engine CSVs: private WS, store size, refaults, focus latency,
     engine CPU/memory. "Not applicable" is fine for docs or UI-only changes. -->

## Checklist

- [ ] `dotnet build RamPilot.sln -c Release -warnaserror` and `dotnet test` pass locally.
- [ ] `dotnet format RamPilot.sln --verify-no-changes` passes.
- [ ] Safety rules hold: never cap focused / audio / deny-listed processes, fail toward "no limit", no new privileges or network calls.
- [ ] IPC changes bump the protocol version and update `docs/IPC.md`.
- [ ] Savings-accounting changes update `docs/DESIGN.md`.
- [ ] User-visible changes have a line under `Unreleased` in `CHANGELOG.md`.
- [ ] No local artifacts, logs, CSVs or personal paths are committed.
