# Contributing to RamPilot

Thanks for your interest. RamPilot changes how much memory other programs may keep resident, so the bar for
changes is correctness and evidence, not feature count.

## Ways to help

- **Bug reports with data.** OS build, RAM size, which apps were managed, what you saw, and the relevant lines
  from `%LOCALAPPDATA%\RamPilot\` (log and measurement CSV). Use the bug report template.
- **Measurements.** Runs of the [benchmark](bench/README.md) on hardware other than the author's, including
  results that show RamPilot does not help. Negative results are welcome and will be published.
- **Code.** Fixes and improvements, preferably discussed in an issue first if they change controller behaviour,
  the safety model or the IPC protocol.
- **Translations** of the panel UI. The engine sends only codes and structured fields; all user-facing text
  lives in the panel.

## Development setup

- Windows 10 21H2+ or Windows 11, x64.
- .NET 10 SDK (version pinned in [`global.json`](global.json)).
- Visual Studio 2022/2026 Build Tools with "Desktop development with C++" (Native AOT needs the MSVC linker).
- WebView2 Runtime.

```powershell
.\build.ps1                                    # build, test, publish to artifacts\
dotnet build RamPilot.sln -c Release           # build only
dotnet test RamPilot.sln -c Release            # tests only
dotnet format RamPilot.sln --verify-no-changes # style check, as in CI
```

Warnings are errors (`Directory.Build.props`). Run `dotnet format RamPilot.sln` before committing.

Never commit `artifacts/`, `dist/`, logs or measurement CSVs from your machine. They contain local paths and
process names.

## Ground rules for changes

1. **Fail toward "no limit".** Any path that cannot decide safely must release the cap or leave the process
   alone. Unknown process role means excluded.
2. **Never cap the foreground app**, an app with an active audio session, or anything on the deny list. The deny
   list is compiled in and must not become user-overridable.
3. **No new privileges.** No admin requirement, no driver, no code injection, no writes to other processes'
   memory, no network calls from the engine.
4. **Controller logic stays pure and tested.** Decisions take state, a sample and configuration, and return an
   action. New behaviour comes with tests, including the invariants: never cap a focused process, never go
   below the floor, always release on exit.
5. **Honest numbers.** Do not change how savings are computed without updating
   [`docs/DESIGN.md`](docs/DESIGN.md) and explaining the estimator. The headline stays net of the compression
   store.
6. **Protocol changes** bump the protocol version in [`docs/IPC.md`](docs/IPC.md) and keep the panel tolerant
   of unknown fields and event codes.
7. **English** for identifiers, comments, logs, CLI flags and commit messages. UI strings are localized in the
   panel.

## Pull requests

- One logical change per PR. Keep diffs small enough to review.
- Fill in the PR template, including how you tested and, for controller changes, before/after measurements.
- CI must be green: build with warnings as errors, tests, `dotnet format`, Native AOT publish and the engine
  size budget.
- Add a line under `Unreleased` in [`CHANGELOG.md`](CHANGELOG.md) for user-visible changes.
- By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).

## Security issues

Do not open a public issue. See [`SECURITY.md`](SECURITY.md).

## Conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
