# Engine ↔ Panel protocol (v2)

**Transport:** named pipe `\\.\pipe\RamPilot.<SessionId>` (Windows session id of the engine process, e.g. `RamPilot.1`).
- Server: `PipeOptions.CurrentUserOnly`, bounded instance count (≤ 4), request lines ≤ 4 KiB (longer → connection closed).
- Client: `PipeOptions.CurrentUserOnly`, `TokenImpersonationLevel.Identification`.

**Framing:** one UTF-8 JSON object per line (`\n`) per request, one per reply. A connection may be kept open; the panel
polls `status` once per second. The engine never initiates messages.

**Language:** the engine sends **no human-readable UI text**. Events carry a `code` + structured fields; the panel
localizes (English, Turkish). Log files may contain English text.

## Requests

| `cmd` | fields | reply |
|---|---|---|
| `hello` | `protocol` (int, client's version) | `{"ok":true,"protocol":2,"engineVersion":"0.1.0"}` |
| `status` | — | `Status` |
| `pause` / `resume` | — | `{"ok":true}` — pause releases every cap; measuring continues |
| `setApp` | `exe` (string, must be in allowlist), `enabled` (bool) | `{"ok":true}` |
| `setAutostart` | `enabled` (bool) | `{"ok":true}` |
| `quit` | — | `{"ok":true}` — releases every cap, then exits |

Errors: `{"ok":false,"error":"<machine-readable-code>"}` — codes: `bad_request`, `unknown_cmd`, `not_allowed`,
`protocol_mismatch`. The panel must send `hello` first and show an "update required" state on mismatch.

## Status

```json
{
  "protocol": 2,
  "engineVersion": "0.1.0",
  "paused": false,
  "autostart": true,
  "uptimeSec": 742,
  "engine": { "wsMb": 12.2, "privateMb": 5.1 },
  "system": { "totalMb": 15996, "availMb": 7420, "compressionMb": 228, "loadPct": 54, "commitPct": 61 },
  "governor": { "pressure": "low", "aggressiveness": 0.35 },
  "savings": { "reclaimedMb": 640.0, "storeCostMb": 150.0, "netMb": 490.0 },
  "apps": [
    {
      "exe": "WhatsApp.Root.exe",
      "name": "WhatsApp",
      "enabled": true,
      "running": true,
      "focused": false,
      "audio": false,
      "state": "Holding",
      "procCount": 9,
      "wsMb": 210.0,
      "privateWsMb": 96.0,
      "capMb": 180.0,
      "reclaimedMb": 281.0
    }
  ],
  "events": [
    { "t": 1794470467, "kind": "cap", "code": "cap_lowered", "app": "WhatsApp.Root.exe", "role": "renderer",
      "capMb": 64, "prevCapMb": 71 }
  ],
  "history": [ { "t": 1794470400, "availMb": 7420, "managedMb": 640, "netMb": 490 } ]
}
```

### Field semantics
- `state` (per app): `Free` (no cap; focused, active, audio, recently used or below size threshold) · `Descending`
  (cap being lowered gradually) · `Holding` (cap at learned level) · `Mixed` (processes of the app differ) ·
  `Disabled` (user disabled, or engine paused) · `NotRunning`.
- `savings` — honest accounting, all ≥ 0:
  - `reclaimedMb`: Σ over managed processes of (private WS the process would have now without a cap, estimated
    from its own uncapped trajectory) − (current private WS). Never credits the app's own shrinking.
  - `storeCostMb`: estimated growth of the Memory Compression store attributable to RamPilot's trims.
  - `netMb` = `reclaimedMb` − `storeCostMb`. The panel's headline number is `netMb`.
  - Estimation methods are documented in `docs/DESIGN.md`.
- `apps[].reclaimedMb`: gross per app (store cost is only known system-wide).
- `governor.pressure`: `low` | `medium` | `high`; `aggressiveness` 0..1 scales how far caps descend.
- `history`: last 30 min, 10 s spacing (≤ 180 points), `t` = Unix seconds.
- `events`: last 50, oldest first, `t` = Unix seconds.

### Event codes
| code | kind | extra fields |
|---|---|---|
| `engine_started` | info | — |
| `paused` / `resumed` | info | — |
| `app_enabled` / `app_disabled` | info | `app` |
| `autostart_on` / `autostart_off` | info | — |
| `descent_started` | cap | `app`, `role`, `capMb` |
| `cap_lowered` | cap | `app`, `role`, `capMb`, `prevCapMb` |
| `cap_raised` | warn | `app`, `role`, `capMb`, `prevCapMb`, `reason` (`refaults` \| `stall`) |
| `cap_released` | free | `app`, `role`, `reason` (`focus` \| `active` \| `audio` \| `paused` \| `disabled` \| `error`) |
| `cap_restored_learned` | cap | `app`, `role`, `capMb` |
| `cap_tampered` | warn | `app`, `role` — target removed the hard limit itself; re-applied |
| `leftover_cleared` | warn | `app`, `count` — hard limits from a previous crashed run were removed at startup |
| `error` | warn | `detail` (English, for logs) |

The panel must ignore unknown fields and unknown event codes (show a generic line).
