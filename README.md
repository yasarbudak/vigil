# Vigil

Vigil keeps Teams and Slack from showing you as "Away" while you are still at your desk, and does nothing else.

## How it differs from a jiggler

A mouse jiggler moves the pointer on a timer, whether you need it or not. Vigil reads the operating system's
user idle time and acts only when idle time reaches a threshold (default 4 minutes; Teams goes Away after about 5).
It then injects one invisible input event and re-reads idle time to verify the counter was reset. If it was not
(permission revoked, event blocked) Vigil reports a `Degraded` state instead of failing silently.

## Platform status

| Platform | Status |
| --- | --- |
| macOS (Intel and Apple silicon) | Phase 1: daemon and CLI. Tested on Intel (x86_64). |
| Windows, Linux | Planned. `vigil` exits with a clear "not supported yet" message. |
| Tray UI, install/autostart | Planned, not part of Phase 1. |

**Known limitation: `PauseOnLock` does nothing on macOS in Phase 1.** Vigil cannot detect a locked screen there
(the required key is not in the public SDK headers), so it keeps injecting while the screen is locked even though
`PauseOnLock` defaults to `true`. Vigil logs a warning at daemon startup, and `vigil diag` shows the same warning
and reports the lock state as "unknown". Proper lock detection is a pending decision.

## Usage

```
vigil run                    # start the daemon in the foreground (Ctrl+C to stop)
vigil status                 # what the daemon is doing
vigil pause 15m              # also 30s, 1h, 1h30m
vigil pause --until-tomorrow # until the start of tomorrow's schedule window (00:00 by default)
vigil resume
vigil diag                   # read-only report: platform, idle time, permission, lock/battery, config
vigil permissions request    # macOS: ask for the permission Vigil needs (may show a system prompt)
```

Vigil never starts itself. Run `vigil run` in a terminal, or wire it into a launcher of your own choosing.

### States

`Watching` (you are active), `Idle` (threshold reached, injecting), `Injected` (last injection verified and you
have not been active since), `Paused` (manual, outside schedule, locked screen or battery), `Degraded` (the idle
counter could not be reset; see the reason in `vigil status`).

### Configuration

Optional file `config.json` in the per-user directory (macOS: `~/Library/Application Support/Vigil/`). A missing
file means defaults; an invalid file stops the daemon with a list of problems.

```json
{
  "Vigil": {
    "Threshold": "00:04:00",
    "PollInterval": "00:00:05",
    "VerifyDelay": "00:00:00.500",
    "DegradedRetryInterval": "00:01:00",
    "PauseOnLock": true,
    "PauseOnBattery": false,
    "Schedule": { "ActiveDays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], "Start": "09:00", "End": "18:00" },
    "ActivityLog": { "Enabled": true }
  },
  "MacOS": { "Strategy": "MouseNudge" }
}
```

`Schedule` is optional (default: always). If `Start` is later than `End` the window crosses midnight and belongs
to the day it starts on. `MacOS.Strategy` is `MouseNudge` (+1 px and back) or `F15KeyUp` (key-up of F15 only).
Set `VIGIL_DATA_DIR` to use a different data directory.

## macOS permission

Posting input events requires one permission, which Vigil checks silently (`vigil diag`) and requests only when
you run `vigil permissions request`. Allow it in System Settings, Privacy & Security. If it is missing, Vigil
shows `Degraded (PermissionMissing)` and injects nothing.

## Privacy & trust

Vigil must be safe for anyone to run. These are guarantees of the code, not intentions.

**What Vigil does**

- Reads one number from the OS: how long since the last input event (idle time).
- Posts one invisible input event when idle time reaches the threshold, then reads idle time again to verify.
- Talks to its own CLI over a Unix domain socket in your user directory, readable and writable by you only.
- Optionally keeps a daily counter file (number of injections, time covered) in the same directory.

**What Vigil never does**

- No network. No HTTP client, no listening or outgoing network sockets, no update checks, no telemetry. The only
  socket is the local Unix domain socket used by the CLI.
- No reading of what you do. No event taps, no keystroke, mouse, window-title or screen reading. On macOS it does
  not need, and never asks for, Input Monitoring or Screen Recording.
- No elevated rights. It never needs root or administrator access and runs with your user rights in your session.
- No data leaves your machine. The activity log holds counters and timestamps only, no content, and lives in the
  single per-user directory. Set `Vigil.ActivityLog.Enabled` to `false` to store nothing.
- No silent persistence. It never installs autostart, LaunchAgents or login items. Phase 1 has no install command.

**Permissions requested**

| Permission | Why |
| --- | --- |
| macOS permission to post input events | Required to inject the one event that resets the idle counter. Requested only by `vigil permissions request`. |

Nothing else is requested.

## Build and run

See [docs/DEVELOPER.md](docs/DEVELOPER.md).
