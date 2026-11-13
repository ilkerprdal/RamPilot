# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability** (private vulnerability reporting). Do not open a public issue.

Include the RamPilot version, Windows build, steps to reproduce and the impact you expect. We aim to
acknowledge reports within 7 days and to agree on a disclosure date with you. RamPilot is maintained by
volunteers; there is no bug bounty.

## Supported versions

Only the latest release receives security fixes. Before 1.0, fixes are not backported.

## Threat model

### What the engine can touch

`RamPilot.exe` runs as the signed-in user. Its manifest requests no elevation (`asInvoker`), so started normally it
runs at medium integrity, without administrator rights. Nothing prevents a user from starting it elevated; that
gains nothing and is not supported.

- **Target processes:** only processes of allowlisted apps (the configured executables and their Chromium /
  WebView2 helper processes), in the user's own session. Started normally (not elevated), Windows will not
  let it open elevated processes, services or other users' processes.
- **Rights used on targets:** limited query rights (memory counters, and the command line to identify a
  helper's role) and `PROCESS_SET_QUOTA` to set or clear a hard working-set maximum. The engine does not write
  to other processes' memory, create threads in them, load code into them, suspend them or change their
  priority.
- **Effect on targets:** a hard working-set limit can slow a target down if it is set too low (the target
  faults pages back from the compression store or disk). It cannot corrupt the target's memory or change its
  behaviour otherwise. Limits are released on focus, activity, audio, pause, exit and engine failure.
- **Deny list:** games, anti-cheat and security software are compiled into a deny list that configuration
  cannot override. Limiting such processes can look like tampering to anti-cheat systems or interfere with
  security products, so they are never touched, even if a user allowlists them.
- **Guardian:** a separate guardian process clears all limits if the engine terminates unexpectedly. At startup
  the engine also clears hard limits left on allowlisted processes by a previous run.
- **System changes:** the engine writes only under `%LOCALAPPDATA%\RamPilot\` and, if the user enables it, a
  per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value. It installs no service, driver,
  scheduled task or system-wide setting.
- **Network:** the engine opens no network connections.

### Engine-panel pipe

- Name: `\\.\pipe\RamPilot.<SessionId>`, one per Windows session.
- Server and client both use `PipeOptions.CurrentUserOnly`. The client connects with
  `TokenImpersonationLevel.Identification`, so the server cannot impersonate it.
- The server accepts a bounded number of instances (at most 4) and request lines of at most 4 KiB; longer lines
  close the connection.
- Commands are a fixed allowlist (`hello`, `status`, `pause`, `resume`, `setApp`, `setAutostart`, `quit`).
  `setApp` only accepts executables already in the allowlist; it cannot add new targets. There is no command
  that runs code, reads files or sets an arbitrary limit. See [`docs/IPC.md`](docs/IPC.md).

**Accepted risk:** any process running as the same user can connect to the pipe and pause, resume, quit or
toggle allowlisted apps, and could create the pipe first to feed the panel false status data. Code running as
the same user can already do far more than that, so this is inside the trust boundary. The panel renders
pipe data as text only.

### Panel

`RamPilot.Panel.exe` hosts WebView2 with local UI files only. It has a restrictive Content Security Policy,
blocks navigation away from the local UI, renders engine data with `textContent` (no HTML injection), and
exposes only an allowlisted set of bridge commands, each rebuilt field by field before it is sent to the engine.

### Configuration

`config.json` is writable by the user and by any process running as that user. Values are clamped to safe
ranges on load; the deny list is not part of the file. A malicious same-user process editing the file could
add allowlisted apps, which at worst slows those apps down. That is inside the trust boundary above.

### Supply chain

Releases are built by GitHub Actions from a tagged commit, published with a `SHA256SUMS.txt` file and a
GitHub build-provenance attestation (`gh attestation verify`). The binaries are not yet code-signed.
Dependencies are kept current with Dependabot.

### Out of scope

- Attackers who already run code as the same user or as an administrator.
- Performance degradation of an app the user explicitly allowlisted, unless it bypasses a safety rule
  (for example a cap applied to a focused app, an audio session, a deny-listed process, or a cap that survives
  the engine). Those are security-relevant bugs; please report them.
- Behaviour of the WebView2 Runtime itself; report those to Microsoft.
