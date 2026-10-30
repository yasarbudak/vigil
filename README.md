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
| macOS (Intel and Apple silicon) | Daemon, CLI and explicit autostart. Tested on Intel (x86_64); the arm64 package script is not yet run on Apple silicon. |
| Windows, Linux | Planned. `vigil` exits with a clear "not supported yet" message. |
| Autostart (`vigil install`) | macOS only, explicit command. Other platforms: "not supported yet". |
| Tray UI | Planned. |

**Screen lock detection (macOS).** `PauseOnLock` (default `true`) works by polling the window server session
dictionary (`CGSessionCopyCurrentDictionary`). The lock flag in it, `CGSSessionScreenIsLocked`, is a private key:
it is not in the public SDK headers (it is defined in Apple's open-source xnu as
`kIOConsoleSessionScreenIsLockedKey`), so Apple could change it without notice. Vigil therefore never assumes
"unlocked" when it cannot be sure: if the dictionary is unavailable or lacks the public session keys, the state is
reported as unknown. A session that is not on the console (fast user switching) counts as locked. When the state is
unknown, Vigil keeps injecting (it never stops keeping you present because it cannot tell), and says so: it logs a
warning at daemon startup, again when the state becomes unknown while running, and an info line when it becomes
known again (once per change, not per poll). `vigil diag` shows `Screen locked: unknown` and `vigil status` shows
`Screen lock: UNKNOWN ...` while it is unknown.

## Usage

```
vigil run                    # start the daemon in the foreground (Ctrl+C to stop)
vigil status                 # what the daemon is doing
vigil pause 15m              # also 30s, 1h, 1h30m
vigil pause --until-tomorrow # until the start of tomorrow's schedule window (00:00 by default)
vigil resume
vigil diag                   # read-only report: platform, idle time, permission, lock/battery, config
vigil permissions request    # macOS: ask for the permission Vigil needs (may show a system prompt)
vigil install [--force]      # macOS: start Vigil at login (LaunchAgent); see "Autostart"
vigil uninstall [--purge]    # macOS: stop Vigil and remove the LaunchAgent; --purge also deletes the data directory
```

Vigil never starts itself. Run `vigil run` in a terminal, wire it into a launcher of your own choosing, or run
`vigil install` once to have macOS start it at login. Nothing installs autostart unless you run that command.

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
    "ActivityLog": { "Enabled": true, "RetentionDays": 30 }
  },
  "MacOS": { "Strategy": "MouseNudge" }
}
```

`Schedule` is optional (default: always). If `Start` is later than `End` the window crosses midnight and belongs
to the day it starts on. `MacOS.Strategy` is `MouseNudge` (+1 px and back) or `F15KeyUp` (key-up of F15 only).
`ActivityLog.RetentionDays` (default 30, allowed 1 to 3650) limits how long daily activity files are kept: at
daemon start and after each midnight Vigil deletes its own `activity-yyyy-MM-dd.jsonl` files whose day is more
than that many days before today (with 30, today and the 30 days before it are kept). It touches only files with
exactly that name in the data directory; other files, subdirectories and symbolic links are never touched.
Set `VIGIL_DATA_DIR` to use a different data directory.

## Logs

The daemon always writes its log to `vigil.log` in the data directory (macOS: `~/Library/Application Support/Vigil/`),
whether started by `vigil run` or by the LaunchAgent; `vigil run` also prints to the terminal. The file is capped at
1 MiB: when a line would go over, `vigil.log` becomes `vigil.log.1` (replacing the previous one) and a new
`vigil.log` starts. At most one backup is kept, so logs use about 2 MiB of disk. A `vigil.log` that is already
over the cap at daemon start is rotated first. Both files are 0600. Startup errors (such as an invalid
`config.json`) and unhandled exceptions are written there too. Follow it with `tail -f vigil.log`.

## Autostart (macOS)

`vigil install` is the only thing that ever creates an autostart entry, and only when you run it. It must be run
from inside the app bundle, because macOS ties the permission to post input events to the app bundle (its
identifier and code signature), not to a loose binary. Build the bundle with `scripts/package-macos.sh osx-x64`
(or `osx-arm64`), copy `artifacts/<rid>/Vigil.app` where you want it to live (for example `~/Applications`), then:

```
/path/to/Vigil.app/Contents/MacOS/vigil install
```

What it does, and nothing else:

- Writes `~/Library/LaunchAgents/io.github.yasarbudak.vigil.plist` (0644, written atomically). It starts
  `<that app>/Contents/MacOS/vigil run` at login and restarts it only if it crashes (at most once every 30 s).
  A deliberate exit, such as `config.json` being invalid (exit code 2), is not restarted, so there is no restart loop.
- Creates the data directory and `vigil.log` in it (0600). The daemon writes its log there itself, and launchd
  discards stdout and stderr. See "Logs" below.
- Loads the agent with `launchctl bootstrap gui/<your uid>`, so it also runs right away.

`vigil install` refuses to run outside an app bundle, and refuses when an entry already exists that differs from
what it would write (another app location, another data directory) unless you pass `--force`. Running it again
from the same app with the same settings does nothing. If you move the app, run `vigil install --force` from the
new location. If `VIGIL_DATA_DIR` is set when you install, the same value
is stored in the plist so the daemon uses the same directory as your CLI.

**Every rebuild changes the ad-hoc signature**, and macOS then treats the app as a new one: enable it again in
System Settings, Privacy & Security, Accessibility (remove the old Vigil entry first if it is listed). Run
`vigil diag` to check `Post events`.

### Removing Vigil completely

1. `/path/to/Vigil.app/Contents/MacOS/vigil uninstall --purge` stops the daemon, deletes the LaunchAgent plist and
   deletes `~/Library/Application Support/Vigil`. Without `--purge` the data directory is kept. `--purge` deletes
   only that default directory; if `VIGIL_DATA_DIR` points somewhere else it refuses and tells you so. Stop any
   `vigil run` you started by hand first.
2. Delete `Vigil.app`.
3. Remove Vigil from System Settings, Privacy & Security, Accessibility.

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
- Optionally keeps a daily counter file (number of injections, time covered) in the same directory, and deletes
  its own daily files after `ActivityLog.RetentionDays` (default 30).

**What Vigil never does**

- No network. No HTTP client, no listening or outgoing network sockets, no update checks, no telemetry. The only
  socket is the local Unix domain socket used by the CLI.
- No reading of what you do. No event taps, no keystroke, mouse, window-title or screen reading. On macOS it does
  not need, and never asks for, Input Monitoring or Screen Recording.
- No elevated rights. It never needs root or administrator access and runs with your user rights in your session.
- No data leaves your machine. The activity log holds counters and timestamps only, no content, and lives in the
  single per-user directory and is deleted after `Vigil.ActivityLog.RetentionDays` days. Set
  `Vigil.ActivityLog.Enabled` to `false` to store nothing.
- No silent persistence. It never installs autostart, LaunchAgents or login items on its own. The only way one is
  created is you running `vigil install`; `vigil uninstall` removes it again. No other command, and no daemon or
  update code path, touches `~/Library/LaunchAgents`.

**Permissions requested**

| Permission | Why |
| --- | --- |
| macOS permission to post input events | Required to inject the one event that resets the idle counter. Requested only by `vigil permissions request`. |

Nothing else is requested.

## Build and run

See [docs/DEVELOPER.md](docs/DEVELOPER.md).
