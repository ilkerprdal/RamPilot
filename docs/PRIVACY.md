# Privacy

**Short version:** RamPilot collects nothing and sends nothing. Everything it records stays in a folder on your
machine that you can read and delete.

## No telemetry, no network

- The engine (`RamPilot.exe`) makes no network connections: no telemetry, no crash reporting, no update checks.
- The panel (`RamPilot.Panel.exe`) loads only its own local UI files. It does not load remote content and blocks
  navigation away from the local UI.
- The engine and the panel talk over a local named pipe that is restricted to your user account
  (see [`IPC.md`](IPC.md)).

## What is stored locally

All files are under `%LOCALAPPDATA%\RamPilot\` (for example `C:\Users\<you>\AppData\Local\RamPilot\`).

| File | Contents |
|---|---|
| `config.json` | Your settings: allowlisted apps, per-app on/off, tuning values. |
| Log file(s) | Timestamped engine events: start/stop, limits set and released, errors. Includes **executable names and process IDs** of managed apps. Rotated by size. |
| Measurement CSVs | Periodic samples for managed processes: executable name, process ID, process role (for example `renderer`), working set, private working set, limit, fault rates, CPU, and system available memory and compression-store size. Old files are pruned by the engine. |
| Learned state | Per-app, per-role limits the controller has learned. Executable names only. |
| Cap journal | Which processes currently have a limit, so they can be cleaned up after a crash. Process IDs and start times. |

These files never leave your machine unless you attach them to a bug report yourself. Before you do, look at
them: process names can reveal which apps you use.

RamPilot does not record window titles, file names, command-line arguments beyond what is needed to classify a
helper process in memory, keystrokes, screen contents or anything typed into the apps it manages.

If you enable "Start with Windows", RamPilot writes one value named `RamPilot` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## WebView2 Runtime

The panel's UI is rendered by Microsoft Edge WebView2 Runtime, a shared Windows component maintained and updated
by Microsoft. RamPilot does not enable any WebView2 feature that sends data. The runtime itself may update in the
background and may send diagnostic data to Microsoft according to your Windows diagnostic data settings and the
[Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). That behaviour is outside
RamPilot's control. The engine does not use WebView2; if you never open the panel, WebView2 is not started by
RamPilot.

## Removing all data

1. Turn off "Start with Windows" in the panel (or delete the `RamPilot` value under the `Run` key above).
2. Exit RamPilot from the tray. All limits are released on exit.
3. Delete the program folder and `%LOCALAPPDATA%\RamPilot\`.
