# Vigil

[![CI](https://github.com/yasarbudak/vigil/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/yasarbudak/vigil/actions/workflows/ci.yml)

Vigil keeps chat apps such as Teams and Slack from flipping to "Away" while you are at your desk but not touching
the keyboard or mouse: reading, in a call, thinking.

Timer-based mouse jigglers move the pointer at fixed intervals whether you need it or not. Vigil waits until the
operating system's idle counter reaches a threshold, posts a single invisible input event, and then re-reads the idle
counter to verify that the event really reset it. If it did not, Vigil reports a `Degraded` state and retries
instead of failing silently. It runs as a small background daemon controlled by a command-line interface.

**Status:** macOS works today (verified on Intel; an Apple silicon build exists but has not been tested on
hardware). Windows and Linux (X11 and Wayland via uinput) are planned. A tray UI is planned. Nothing else is
implemented yet: on other platforms `vigil` exits with a "not supported yet" message.

## Contents

- [How it works](#how-it-works)
- [Measured results](#measured-results)
- [Privacy and trust](#privacy-and-trust)
- [Requirements and build from source](#requirements-and-build-from-source)
- [Install on macOS](#install-on-macos)
- [CLI reference](#cli-reference)
- [Configuration](#configuration)
- [Files Vigil creates](#files-vigil-creates)
- [Logs](#logs)
- [Uninstall](#uninstall)
- [Troubleshooting](#troubleshooting)
- [Roadmap](#roadmap)
- [Contributing and development](#contributing-and-development)
- [License](#license)

## How it works

The daemon polls the OS idle counter every `PollInterval` (default 5 s). While the idle time is below `Threshold`
(default 4 minutes; Teams goes Away after about 5) it does nothing. When the idle time reaches the threshold it:

1. Posts one invisible event, chosen by the injection strategy:
   - `MouseNudge` (default): moves the pointer +1 px and straight back, so the net movement is zero.
   - `F15KeyUp`: posts only the key-up of F15, an unassigned key.
2. Waits `VerifyDelay` (default 500 ms) and re-reads the idle counter. The injection counts as verified when the
   idle time is below `VerifyDelay` plus 1 second.
3. If verification fails, the post fails, or the permission is missing, the state becomes `Degraded` with the reason
   (`PermissionMissing`, `InjectionFailed` or `VerificationFailed`). After a failed or unverified injection Vigil
   retries no more often than every `DegradedRetryInterval` (default 1 minute). A later verified injection recovers
   automatically.

Real activity is whatever the idle counter shows that is not Vigil's own injection.

Vigil pauses (state `Paused`) in these cases, evaluated in this order:

1. Manual pause (`vigil pause`).
2. Outside the configured schedule window.
3. Screen locked (`PauseOnLock`, default `true`).
4. Running on battery power (`PauseOnBattery`, default `false`).

### States

| State | Meaning |
| --- | --- |
| `Watching` | You are active, or nothing needs doing. |
| `Idle` | The threshold was reached and an injection is due (shown until the result is recorded). |
| `Injected` | The last injection was verified and you have not been active since. |
| `Paused` | Vigil is deliberately not acting. The reason is shown: `Manual`, `OutsideSchedule`, `ScreenLocked` or `OnBattery`. |
| `Degraded` | The idle counter could not be reset. The reason is shown. |

### Screen lock detection (macOS)

Lock detection polls the window server session dictionary (`CGSessionCopyCurrentDictionary`). The lock flag in it,
`CGSSessionScreenIsLocked`, is a private key: it is not in the public SDK headers (it is defined in Apple's
open-source xnu as `kIOConsoleSessionScreenIsLockedKey`), so Apple could change it without notice. Vigil therefore
never assumes "unlocked" when it cannot be sure. If the dictionary is unavailable or lacks the public session keys,
the state is reported as unknown. A session that is not on the console (fast user switching) counts as locked.

When the state is unknown, Vigil keeps injecting rather than stopping, and says so: it logs a warning at daemon
startup, again when the state becomes unknown while running, and an info line when it becomes known again (once per
change, not per poll). `vigil diag` shows `Screen locked: unknown` and `vigil status` shows `Screen lock: UNKNOWN ...`
while it is unknown.

### Activity counters

Vigil counts injections and "covered" time per day, which `vigil status` shows as `Today`. Covered time is the
user-absent time kept alive by verified injections. It excludes pauses, closed schedule windows, locked screens,
sleep, and any stretch in which injections were failing.

## Measured results

These were measured by the author on macOS 15.8.1 (Intel), on 2026-10-28 and 2026-10-29. They describe one machine
and one chat client, not a guarantee for every setup.

- Without Vigil, Teams went Away after 5 minutes of idle time.
- With Vigil (4 minute threshold, `MouseNudge`), Teams stayed Available through a 9-minute idle window.
- Both `MouseNudge` and `F15KeyUp` reset the HID idle counter.
- Screen lock detection reported the screen as locked within about 1 second of locking.
- Started by launchd, the first injection fired 4 minutes after the last activity and was verified.

## Privacy and trust

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
| macOS permission to post input events (listed under Accessibility in System Settings) | Required to inject the one event that resets the idle counter. Requested only by `vigil permissions request`. |

Nothing else is requested.

**Verify it yourself**

- Networking: `grep -rn "System.Net" src/` should show only `System.Net.Sockets`, used for the local Unix domain
  socket in `src/Vigil/Ipc`. `Directory.Build.targets` removes the `System.Net.Http` implicit using from every
  project. The test projects also disable the test host's telemetry extension (see `docs/DEVELOPER.md`).
- Permissions: the only macOS permission APIs used are `CGPreflightPostEventAccess` and `CGRequestPostEventAccess`
  (`src/Vigil.Platform.MacOS/MacOSPermissions.cs`). You can see what is granted under System Settings, Privacy &
  Security: Vigil should appear only under Accessibility.
- Entitlements: the packaging script (`scripts/package-macos.sh`) signs the app ad hoc and passes no entitlements.
  Inspect the built app with `codesign -d --entitlements - ~/Applications/Vigil.app`.
- Autostart: `LaunchAgentManager` is called only from `AutostartCommand` (`vigil install` and `vigil uninstall`).
- Files: everything Vigil writes is listed in [Files Vigil creates](#files-vigil-creates).

## Requirements and build from source

- macOS 12.0 or later (`LSMinimumSystemVersion` in the app bundle).
- The .NET SDK version pinned in `global.json` (10.0.201, rolling forward to the latest feature band). Releases are
  built with Native AOT, so the platform's native toolchain for .NET Native AOT must be available.

```
git clone https://github.com/yasarbudak/vigil.git
cd vigil
scripts/package-macos.sh osx-x64      # Intel
scripts/package-macos.sh osx-arm64    # Apple silicon (not yet tested on hardware)
```

The script publishes a Native AOT binary, assembles an app bundle, and signs it ad hoc. The result is
`artifacts/<rid>/Vigil.app` (for example `artifacts/osx-x64/Vigil.app`).

## Install on macOS

Prebuilt, unsigned (ad-hoc signed) zips for Intel and Apple silicon will be attached to the
[Releases](https://github.com/yasarbudak/vigil/releases) page once a version is tagged. Until then, build from source as
described above. Unzip a release and continue with step 1 below, using the unzipped `Vigil.app` instead of
`artifacts/osx-x64/Vigil.app`.

Vigil never starts itself. You can run `vigil run` in a terminal, or install the LaunchAgent once with
`vigil install` so macOS starts it at login.

1. **Copy the app to `~/Applications`.**

   ```
   mkdir -p ~/Applications
   ditto artifacts/osx-x64/Vigil.app ~/Applications/Vigil.app
   /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f ~/Applications/Vigil.app
   ```

   Vigil appears in the Accessibility list only when the app is registered with Launch Services from a regular
   location such as `~/Applications`; `lsregister -f` does that registration right away.

2. **Grant the permission.** Open System Settings, Privacy & Security, Accessibility, and add `Vigil.app` from
   `~/Applications` and switch it on. macOS ties the permission to the app bundle (its identifier and code
   signature), so the permission belongs to `Vigil.app`, not to Terminal.

   Because of that, running `vigil diag` from Terminal reports Terminal's permission, not Vigil's. To check the
   bundle's own permission, run diag through the bundle and read the output file:

   ```
   open -W -n -o /tmp/vigil-diag.txt ~/Applications/Vigil.app --args diag
   cat /tmp/vigil-diag.txt
   ```

   `Post events: granted` means the permission is in place.

3. **Install autostart.** Run the install command from inside the bundle:

   ```
   ~/Applications/Vigil.app/Contents/MacOS/vigil install
   ```

   It writes `~/Library/LaunchAgents/io.github.yasarbudak.vigil.plist` (mode 0644, written atomically) and loads it
   with `launchctl bootstrap gui/<your uid>`, so Vigil also starts immediately. The agent runs
   `<app>/Contents/MacOS/vigil run` at login and restarts it only if it crashes (at most once every 30 seconds).
   A deliberate exit, such as an invalid `config.json` (exit code 2), is not restarted, so there is no restart loop.
   `vigil install` also creates the data directory and `vigil.log`.

   `vigil install` refuses to run outside an app bundle. It also refuses when a different Vigil agent entry already
   exists (another app location, another data directory) unless you pass `--force`. Running it again from the same
   app with the same settings changes nothing. If `VIGIL_DATA_DIR` is set when you install, the same value is stored
   in the plist so the daemon and your CLI use the same directory.

4. **Verify.**

   ```
   ~/Applications/Vigil.app/Contents/MacOS/vigil status
   ```

   For convenience you can put that path on your `PATH` or alias it. The examples below write `vigil`.

### Updating

Every rebuild produces a new ad-hoc signature, and macOS then treats the app as a new one. After copying a new build
to `~/Applications`, remove the old Vigil entry in System Settings, Privacy & Security, Accessibility, add Vigil
again and switch it on. If the plist would change (for example you moved the app), run `vigil install --force` from
the new location. Check with `vigil diag` (via the bundle, as above) that `Post events` is `granted`.

## CLI reference

```
vigil run                      Run the daemon in the foreground. Stop it with Ctrl+C.
vigil status                   Show what the running daemon is doing.
vigil pause <duration>         Pause Vigil for a while, e.g. 30s, 15m, 1h, 1h30m.
vigil pause --until-tomorrow   Pause until the start of tomorrow's schedule window.
vigil resume                   End a manual pause.
vigil diag                     Print platform, idle time, permission and configuration.
vigil permissions request      Ask macOS for permission to post input events.
vigil install [--force]        Start Vigil at login (macOS LaunchAgent).
vigil uninstall [--purge]      Stop Vigil and remove its autostart entry.
```

- `run` starts the daemon in the foreground and also prints its log to the terminal. Only one daemon can run per
  data directory.
- `status`, `pause` and `resume` talk to the running daemon and print the resulting status. If no daemon is running
  they print `The Vigil daemon is not running ... Start it with 'vigil run'.` and exit 1.
- `pause` takes either a duration or `--until-tomorrow`, not both and not neither (exit 1 otherwise). Durations are
  one or more number+unit parts with units `s`, `m`, `h` and `d`, for example `90s`, `15m`, `1h30m`, `2d`; the total
  must be greater than zero. `--until-tomorrow` pauses until tomorrow's date at the configured schedule `Start`
  (00:00 with the default schedule).
- `diag` is read only and works without the daemon. It prints the config path, whether the config is valid, the
  effective settings, the current idle time, whether the permission to post events is granted, and whether the
  screen is locked and the machine is on battery (`unknown` when it cannot be determined). It posts nothing and
  shows no prompt. Exit code 0 if the configuration is valid, 2 if it is not.
- `permissions request` is the only command that can make macOS show a permission prompt. It exits 0 if the
  permission is granted and 1 if not.
- `install` must be run from inside `Vigil.app` and does only what is described under
  [Install on macOS](#install-on-macos). `--force` replaces an existing agent entry that differs from what would be
  written.
- `uninstall` is described under [Uninstall](#uninstall).

On platforms other than macOS, `install` and `uninstall` exit 1 with "not supported yet".

### Sample status output

```
State:           Watching
Last activity:   09:37:42 (0s ago)
Last injection:  none yet
Today:           0 injections, 0s covered
Screen lock:     detected, pausing while locked
```

`State` shows the reason for `Paused` (`Paused (Manual)`, `Paused (OutsideSchedule)`, ...) and for `Degraded`. A
manual pause shows its end time, for example `Paused (until 10:15:00)`. `Screen lock` is one of
`detected, pausing while locked`, `UNKNOWN: cannot tell if the screen is locked, NOT pausing while locked`,
`not used (PauseOnLock is off)` or `not checked yet`.

## Configuration

Vigil reads an optional `config.json` from the data directory (macOS: `~/Library/Application Support/Vigil/`, or
`$VIGIL_DATA_DIR/config.json`). A missing file means defaults. It is the only configuration source: environment
variables and command-line options do not configure the daemon.

Full example with every key at its default, except that `Schedule` is shown with a weekday window (without a
`Schedule` section Vigil is active at all times):

```json
{
  "Vigil": {
    "Threshold": "00:04:00",
    "PollInterval": "00:00:05",
    "VerifyDelay": "00:00:00.500",
    "DegradedRetryInterval": "00:01:00",
    "PauseOnLock": true,
    "PauseOnBattery": false,
    "Schedule": {
      "ActiveDays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "Start": "09:00",
      "End": "18:00"
    },
    "ActivityLog": {
      "Enabled": true,
      "RetentionDays": 30
    }
  },
  "MacOS": {
    "Strategy": "MouseNudge"
  }
}
```

### Keys

| Key | Type | Default | Rules |
| --- | --- | --- | --- |
| `Vigil.Threshold` | time span (`hh:mm:ss`) | `00:04:00` | Greater than zero. |
| `Vigil.PollInterval` | time span | `00:00:05` | Greater than zero and shorter than `Threshold`. |
| `Vigil.VerifyDelay` | time span | `00:00:00.500` | Not negative and shorter than `PollInterval`. |
| `Vigil.DegradedRetryInterval` | time span | `00:01:00` | Not shorter than `PollInterval`. |
| `Vigil.PauseOnLock` | boolean | `true` | Pause while the screen is locked. |
| `Vigil.PauseOnBattery` | boolean | `false` | Pause while running on battery power. |
| `Vigil.Schedule.ActiveDays` | array of day names | every day | If given, must not be empty (omit it for every day). Days are `Monday` to `Sunday`. |
| `Vigil.Schedule.Start` | time (`HH:mm`) | `00:00` | Start of the daily window. |
| `Vigil.Schedule.End` | time (`HH:mm`) | `00:00` | End of the daily window. |
| `Vigil.ActivityLog.Enabled` | boolean | `true` | `false` stores nothing; counters then live in memory and restart at zero. |
| `Vigil.ActivityLog.RetentionDays` | integer | `30` | 1 to 3650. |
| `MacOS.Strategy` | `MouseNudge` or `F15KeyUp` | `MouseNudge` | Which invisible event is posted. |

### Schedule semantics

- If `Start` equals `End` (the default, both `00:00`), the window covers the whole day, limited only by
  `ActiveDays`.
- If `Start` is earlier than `End`, the window is `Start` up to but not including `End` on the listed days.
- If `Start` is later than `End`, the window crosses midnight and belongs to the day it starts on: with
  `Start` 22:00, `End` 06:00 and `Friday` listed, the window runs from Friday 22:00 to Saturday 06:00.
- Schedules are wall-clock windows, so daylight saving transitions follow the clock on the wall.

### Activity log retention

At daemon start and after each midnight, Vigil deletes its own `activity-yyyy-MM-dd.jsonl` files whose day is more
than `RetentionDays` days before today (with 30, today and the 30 days before it are kept). It touches only regular
files with exactly that name in the data directory. Other files, subdirectories and symbolic links are never touched.

### Invalid configuration

An unreadable file, malformed JSON, a value of the wrong type, or a value outside the rules above stops the daemon
with a message that lists the problems, and exit code 2. `vigil diag` shows the same problems and also exits 2. Under
the LaunchAgent, exit code 2 is not restarted; the message is written to `vigil.log`.

## Files Vigil creates

All files live in one per-user data directory:

- macOS: `~/Library/Application Support/Vigil/`
- Override: set the `VIGIL_DATA_DIR` environment variable. Use it, for example, to try a configuration without
  touching the real directory. If it is set when you run `vigil install`, the value is stored in the LaunchAgent.

| Path | Purpose | Mode |
| --- | --- | --- |
| the data directory | Holds everything below. | 0700 |
| `config.json` | Your optional configuration. Vigil never writes it. | as you create it |
| `vigil.sock` | Local Unix domain socket the CLI uses to talk to the daemon. | 0600 |
| `vigil.sock.lock` | Lock file that guarantees a single daemon instance. | 0600 |
| `activity-YYYY-MM-DD.jsonl` | Daily activity record: one JSON line per event with timestamps and counters, no content. Deleted after `RetentionDays`. Not written when `ActivityLog.Enabled` is `false`. | 0600 |
| `vigil.log`, `vigil.log.1` | Daemon log and its one backup. See [Logs](#logs). | 0600 |

Outside the data directory, `vigil install` writes
`~/Library/LaunchAgents/io.github.yasarbudak.vigil.plist`, and nothing else does.

## Logs

The daemon always writes its log to `vigil.log` in the data directory, whether started by `vigil run` or by the
LaunchAgent; `vigil run` also prints to the terminal. The file is capped at 1 MiB: when a line would go over,
`vigil.log` becomes `vigil.log.1` (replacing the previous one) and a new `vigil.log` starts. At most one backup is
kept, so logs use about 2 MiB of disk. A `vigil.log` that is already over the cap at daemon start is rotated first.
Both files are 0600. Startup errors (such as an invalid `config.json`) and unhandled exceptions are written there
too. Follow it with:

```
tail -f ~/Library/Application\ Support/Vigil/vigil.log
```

## Uninstall

```
~/Applications/Vigil.app/Contents/MacOS/vigil uninstall            # stop Vigil and remove the LaunchAgent
~/Applications/Vigil.app/Contents/MacOS/vigil uninstall --purge    # ... and also delete the data directory
```

- `vigil uninstall` unloads the LaunchAgent (stopping the daemon it started) and deletes the plist. If nothing is
  installed it says so and does nothing. The data directory is kept.
- `--purge` additionally deletes `~/Library/Application Support/Vigil`, including your `config.json`, the activity
  log and the daemon log. It deletes only that default directory: if `VIGIL_DATA_DIR` points somewhere else, or the
  directory is a symbolic link, it refuses and tells you so. Stop any `vigil run` you started by hand first.

To remove Vigil completely:

1. Run `vigil uninstall --purge` as above.
2. Delete `~/Applications/Vigil.app`.
3. Remove Vigil from System Settings, Privacy & Security, Accessibility.

## Troubleshooting

**`Post events: not granted`, or the state is `Degraded (PermissionMissing)`.** Vigil injects nothing until the
permission is granted. Enable `Vigil.app` under System Settings, Privacy & Security, Accessibility. Remember that
`vigil diag` run from Terminal reports Terminal's permission; check the bundle's own permission with
`open -W -n -o /tmp/vigil-diag.txt ~/Applications/Vigil.app --args diag`.

**Vigil does not appear in the Accessibility list.** Copy `Vigil.app` to `~/Applications` and register it with
`lsregister -f` (see [Install on macOS](#install-on-macos)), then add it again.

**Permission stopped working after an update.** A rebuild changes the ad-hoc signature. Remove the old Vigil entry
in Accessibility, add the new one, and run `vigil install --force` if the LaunchAgent plist changed.

**State is `Degraded (VerificationFailed)` or `Degraded (InjectionFailed)`.** The event was not posted, or the idle
counter did not reset after it. The reason text in `vigil.log` and the activity log gives the detail. Vigil retries
every `DegradedRetryInterval` and recovers on its own once an injection is verified. If the permission was recently
changed, check it first.

**`Another Vigil daemon is already running`.** Only one daemon can run per data directory. Stop the other one
(`launchctl bootout` through `vigil uninstall`, or Ctrl+C for a foreground `vigil run`) before starting another. This
exits with code 1.

**`The Vigil daemon is not running`.** `status`, `pause` and `resume` need a running daemon. Start it with
`vigil run`, or `vigil install` for autostart.

**Configuration errors (exit code 2).** The message names the file and each violated rule. Fix `config.json` and
start the daemon again. `vigil diag` validates the file without starting the daemon.

**`Screen lock: UNKNOWN`.** Vigil could not read the lock state and keeps injecting; see
[Screen lock detection](#screen-lock-detection-macos).

**`vigil install` refuses to run.** Run it from inside the bundle
(`.../Vigil.app/Contents/MacOS/vigil install`), and use `--force` if a different Vigil agent entry exists.

## Roadmap

- Windows support.
- Linux support, X11 and Wayland (via uinput).
- Tray UI.

## Contributing and development

Developer documentation, including the architecture and the rules that protect the trust guarantees above, is in
[docs/DEVELOPER.md](docs/DEVELOPER.md). To build and run the tests:

```
dotnet build -c Release
dotnet test
```

The tests use a fake platform, so they never post real input events.

Please do not run `vigil run`, `vigil permissions request`, `vigil install` or `vigil uninstall` on a development
machine just to see them work: they post input events, may show a system prompt, or change your LaunchAgents.
`vigil diag` is read only.

## License

MIT. See [LICENSE](LICENSE).
