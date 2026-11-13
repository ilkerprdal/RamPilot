# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Before 1.0.0, minor versions may contain breaking
changes to the configuration file and the IPC protocol.

## [Unreleased]

### Added

- First public version, targeted as 0.1.0.
- Tray engine `RamPilot.exe` (C# .NET 10, Native AOT) that sets feedback-controlled hard working-set limits
  on idle background processes of allowlisted apps (Electron / WebView2).
- Gradual descent controller with per-app, per-role learned limits; fast increase on refaults.
- Governor that scales aggressiveness with system memory pressure.
- Safety: foreground, CPU-activity and audio-session release; compiled-in deny list for games, anti-cheat and
  security software; fail-closed process role detection; guardian process that clears limits if the engine
  dies; startup cleanup of leftover limits; `--recover`.
- Honest accounting: reclaimed (gross), estimated compression-store cost and net, reported separately.
- On-demand control panel `RamPilot.Panel.exe` (WebView2), English and Turkish, light and dark themes.
- Engine-panel protocol v2 over a per-session named pipe with `CurrentUserOnly` on both ends
  ([`docs/IPC.md`](docs/IPC.md)).
- Configuration file `%LOCALAPPDATA%\RamPilot\config.json` with clamped values; `--config <path>`.
- CLI: `--minutes N`, `--recover`, `--summary [--detail]`.
- Benchmark methodology and non-admin harness scripts in `bench/`.
- CI (build with warnings as errors, tests, format check, AOT publish, size budget) and tag-driven releases
  with SHA-256 checksums and build-provenance attestation.
