# Vigil

[![CI](https://github.com/yasarbudak/vigil/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/yasarbudak/vigil/actions/workflows/ci.yml)

Vigil keeps chat apps such as Teams and Slack from flipping to "Away" while you are at your desk but not touching
the keyboard or mouse: reading, in a call, thinking.

Timer-based mouse jigglers move the pointer at fixed intervals whether you need it or not. Vigil waits until the
operating system's idle counter reaches a threshold, posts a single invisible input event, and then re-reads the idle
counter to verify that the event really reset it. If it did not, Vigil reports a `Degraded` state and retries
instead of failing silently. It runs as a small background daemon controlled by a command-line interface.

**Status:** macOS works today (verified on Intel; an Apple silicon build exists but has not been tested on
hardware). Windows and Linux are implemented: they build and are unit-tested in CI, and CI also starts each Linux
binary once on a headless runner to check that it reports the missing desktop cleanly. Neither has been verified on a
real Windows machine or a real Linux desktop (X11 or Wayland) yet. A tray icon with a status and settings window comes with
every package (see [Tray and settings window](#tray-and-settings-window)); it has not been verified on a real desktop of any
of the three systems yet. On other platforms `vigil` exits with a "not supported yet" message.

## Contents

- [How it works](#how-it-works)
- [Measured results](#measured-results)
- [Privacy and trust](#privacy-and-trust)
- [Requirements and build from source](#requirements-and-build-from-source)
- [Install on macOS](#install-on-macos)
- [Install on Windows](#install-on-windows)
- [Install on Linux](#install-on-linux)
- [CLI reference](#cli-reference)
- [Tray and settings window](#tray-and-settings-window)
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
   - `MouseNudge` (default): on macOS moves the pointer +1 px and straight back, so the net movement is zero. On
     Windows it posts one relative mouse move of zero pixels, so the pointer does not move at all. On Linux it moves
     the pointer +1 px and straight back, like macOS (the Linux input core drops a zero-sized move, so a zero move is
     not an option); with the pointer at the very edge of the screen the first move is clamped and the pointer ends
     up one pixel off.
   - `F15KeyUp`: posts only the key-up of F15, an unassigned key. With the Linux `uinput` backend it posts the
     press and the release, because the kernel drops the release of a key that is not down.
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

### Screen lock detection (Windows)

Lock detection asks the Remote Desktop Services API for the extended information of the session Vigil runs in
(`WTSQuerySessionInformationW` with `WTSSessionInfoEx`) and reads its `SessionFlags`: locked, unlocked or unknown.
Unknown, or anything the API does not document, is reported as unknown and handled exactly like on macOS (Vigil keeps
injecting and says so). Vigil supports Windows 10 and later; the documentation says the locked and unlocked flags are
reversed on Windows 7 and Windows Server 2008 R2, and that is deliberately not compensated for. A session that is
disconnected or switched away (fast user switching, a closed Remote Desktop window) is not treated as locked.

### Screen lock detection (Linux)

Lock detection reads the locked hint of your login session from systemd-logind
(`org.freedesktop.login1.Session.LockedHint` on the system bus, for the caller's own session). It is a hint: the desktop
environment sets it (`SetLockedHint`) when it locks and unlocks the screen. A desktop that never sets it reads as
unlocked while locked, and Vigil cannot tell that case apart from a really unlocked screen. If logind cannot be reached
or answers with something unexpected, the state is unknown and handled exactly like on macOS (Vigil keeps injecting and
says so).

### Idle time and battery (Linux)

On X11 the idle time is the X server's own counter (MIT-SCREEN-SAVER, libXss). Wayland has no portable way to ask for it:
Vigil reads it from GNOME's compositor (`org.gnome.Mutter.IdleMonitor.GetIdletime`, milliseconds), so **on Wayland only
GNOME is supported**. KDE Plasma has `org.freedesktop.ScreenSaver.GetSessionIdleTime`, but its implementation refuses the
call on Wayland (checked in kscreenlocker's source), and other Wayland compositors only offer a notification protocol,
not a query. On such a session `vigil diag` shows `Idle time: unavailable` with that reason. On Wayland sessions of
other desktops use an X11 session, or wait for a later release.

Battery state comes from the kernel's `/sys/class/power_supply`: any online supply that is not a battery means external
power; otherwise a discharging system battery means "on battery". The battery of a wireless mouse (scope `Device`) is
ignored, a machine without a battery is never on battery, and an unreadable state is unknown.

### Activity counters

Vigil counts injections and "covered" time per day, which `vigil status` shows as `Today`. Covered time is the
user-absent time kept alive by verified injections. It excludes pauses, closed schedule windows, locked screens,
sleep, and any stretch in which injections were failing.

## Measured results

These were measured by the author on macOS 15.8.1 (Intel), on 2026-10-28 and 2026-10-29. They describe one machine
and one chat client, not a guarantee for every setup. Nothing has been measured on Windows or Linux yet.

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
- Talks to its own CLI over a Unix domain socket in your user directory, readable and writable by you only. On
  Windows there are no mode bits: the socket takes the access rules of its folder, `%LOCALAPPDATA%\Vigil` inside your
  profile.
- On Linux, talks to a few local services over D-Bus. D-Bus is inter-process communication over a Unix domain socket on
  your own machine, not network access, and Vigil only ever uses `unix:` bus addresses (a `tcp:` entry in
  `DBUS_SESSION_BUS_ADDRESS` is skipped). The calls are exactly these, and nothing else:
  on the session bus, `org.freedesktop.portal.Desktop` (RemoteDesktop: reads the properties `version` and
  `AvailableDeviceTypes`, then `CreateSession`, `SelectDevices`, `Start`, `NotifyPointerMotion`, `NotifyKeyboardKeysym`,
  `Request.Close` and `Session.Close`; Wayland with the portal backend only) and `org.gnome.Mutter.IdleMonitor`
  (`GetIdletime`; Wayland sessions only); on the system bus, `org.freedesktop.login1` (the property `LockedHint` of your
  own session). It subscribes to the portal's `Response` signal of its own requests and to `Session.Closed` of its own
  session, and to nothing else.
- Optionally keeps a daily counter file (number of injections, time covered) in the same directory, and deletes
  its own daily files after `ActivityLog.RetentionDays` (default 30).
- The tray (`vigil-tray`, optional) is a client of the daemon, nothing more: it talks to it over the same local socket
  (status, pause, resume, reload the configuration) and reads and writes `config.json` in the data directory when you press
  Save in its window. It posts no input, reads no idle time and needs no permission of the operating system. Its one extra
  file is the empty lock file `vigil-tray.lock`, which keeps a second tray icon from starting.

**What Vigil never does**

- No network. No HTTP client, no listening or outgoing network sockets, no update checks, no telemetry. The only
  socket is the local Unix domain socket used by the CLI and the tray. The tray's UI library, Avalonia, is held to the same
  rule: see [Verify it yourself](#privacy-and-trust) below for what was checked, and for the one thing it does at build time
  that this repository switches off.
- No reading of what you do. No event taps, no keystroke, mouse, window-title or screen reading. On macOS it does
  not need, and never asks for, Input Monitoring or Screen Recording. On Windows it installs no hooks
  (`SetWindowsHookEx`) and registers no raw input; it reads one value, the time of the last input event of its session.
  On Linux it never reads the pointer position or any key, selects no X events, records nothing through XRecord or XInput,
  opens no `/dev/input/event*` device and does not ask the portal for screen content: the portal session it opens is
  asked for the keyboard and pointer *to post input*, never for a screen cast or the clipboard.
- No elevated rights. It never needs root or administrator access and runs with your user rights in your session. The
  optional Linux `uinput` backend needs access to `/dev/uinput`, which you grant yourself (a udev rule and a group,
  below); Vigil never installs that rule and never asks for root.
- No data leaves your machine. The activity log holds counters and timestamps only, no content, and lives in the
  single per-user directory and is deleted after `Vigil.ActivityLog.RetentionDays` days. Set
  `Vigil.ActivityLog.Enabled` to `false` to store nothing.
- No silent persistence. It never installs autostart, LaunchAgents, Run keys, systemd units or login items on its own. The
  only way one is created is you running `vigil install`; `vigil uninstall` removes it again. The tray starts and installs
  nothing, not even the daemon, and it adds no login item of its own: how to start it at login is described under
  [Tray and settings window](#tray-and-settings-window). No other command, and no
  daemon or update code path, touches `~/Library/LaunchAgents`, the Windows Run key or `~/.config/systemd/user`. The
  portal consent is the same: the dialog is only ever started by `vigil permissions request` (see below for the one
  caveat).

**Permissions requested**

| Permission | Why |
| --- | --- |
| macOS permission to post input events (listed under Accessibility in System Settings) | Required to inject the one event that resets the idle counter. Requested only by `vigil permissions request`. |
| Windows: none | Windows asks no permission to post input, and Vigil never needs administrator rights. `vigil permissions request` says there is nothing to request. |
| Linux X11: none | The XTest extension needs no permission. `vigil permissions request` only reports whether XTest is usable. |
| Linux Wayland: the desktop portal's RemoteDesktop consent | A dialog of your desktop asks once whether Vigil may control keyboard and pointer. Requested only by `vigil permissions request`. The daemon uses the restore token that the portal returns (saved in `portal-restore-token`) to start its session after a restart without asking again. |
| Linux `uinput` (opt-in): access to `/dev/uinput` | You set it up yourself with a udev rule and a group; Vigil never asks for it. |

Nothing else is requested. On Linux with the portal the daemon never opens the consent dialog by itself: it starts a
session only if a restore token was saved by `vigil permissions request`, and the portal alone decides whether that token
is still valid. If you revoked the consent (or the portal does not know the token), the portal may show the dialog
for the daemon's start request; Vigil gives the portal 15 seconds, closes the request (which removes the dialog), deletes
the saved token and shows `Degraded (PermissionMissing)` until you run `vigil permissions request` again. A consent you
revoked can therefore make a dialog flash once. On Windows, UIPI still applies: Vigil cannot inject input into a window that runs with
higher rights than Vigil (an elevated program in the foreground). Then the injection fails or is not verified and the
state shows `Degraded`.

**Verify it yourself**

- Networking: `grep -rn "System.Net" src/` should show only `System.Net.Sockets`, used for the local Unix domain
  socket in `src/Vigil.App/Ipc` (and, on Linux, the local D-Bus socket). `Directory.Build.targets` removes the
  `System.Net.Http` implicit using from every project. The test projects also disable the test host's telemetry extension
  (see `docs/DEVELOPER.md`).
- The tray's UI library: the three packages the tray references are `Avalonia`, `Avalonia.Desktop` and
  `Avalonia.Themes.Fluent` (version 12.1.4, MIT), listed in `Directory.Packages.props`. Checked at that version: none of the
  Avalonia, Skia or HarfBuzz assemblies references `System.Net.Http`; the only ones that use `System.Net.Sockets` are
  `Avalonia.FreeDesktop.AtSpi` (a Unix domain socket to the accessibility bus), `Avalonia.Remote.Protocol` and
  `Avalonia.DesignerSupport` (TCP, for the design-time previewer, which a running application does not start), and the
  macOS Native AOT tray binary contains none of the TCP or DNS types. Avalonia has one data-collecting piece, at build time
  only: the `Avalonia.BuildServices` package sends anonymous build statistics when a project that references Avalonia is
  compiled, unless the environment variable `AVALONIA_TELEMETRY_OPTOUT=1` is set. Because an environment variable depends on
  each machine, `Directory.Build.targets` clears the property that task is conditioned on, so it never runs, and fails the
  build if that property is ever not empty. On Linux Avalonia's D-Bus code connects to the address in `DBUS_SESSION_BUS_ADDRESS`
  as it is, without Vigil's `unix:`-only rule (Vigil's own D-Bus code refuses anything else); your desktop session sets a `unix:`
  address, and that is the only bus the tray talks to. Nothing in the library collects data at run time, as far as its source
  at that version shows (the search covered network types, URLs, update and telemetry names in the managed code and the macOS
  native code); a built tray was started here only on macOS, with its data directory pointed at a temporary folder.
- Permissions: the only macOS permission APIs used are `CGPreflightPostEventAccess` and `CGRequestPostEventAccess`
  (`src/Vigil.Platform.MacOS/MacOSPermissions.cs`). You can see what is granted under System Settings, Privacy &
  Security: Vigil should appear only under Accessibility.
- Entitlements: the packaging script (`scripts/package-macos.sh`) signs the app ad hoc and passes no entitlements.
  Inspect the built app with `codesign -d --entitlements - ~/Applications/Vigil.app`.
- Windows APIs: the only ones used are `GetLastInputInfo`, `GetTickCount`, `SendInput`, `WTSQuerySessionInformationW`,
  `WTSFreeMemory` and `GetSystemPowerStatus` (`src/Vigil.Platform.Windows/NativeMethods.cs`, each with the page of the
  Microsoft documentation it was checked against), and the per-user Run key through `Microsoft.Win32.Registry`
  (`src/Vigil.Platform.Windows/Autostart/RunKey.cs`). `grep -rn "SetWindowsHook\|RawInput\|GetAsyncKeyState" src/`
  finds nothing.
- Linux system calls: the only libraries loaded are libX11, libXss and libXtst (`XOpenDisplay`, `XDefaultRootWindow`,
  `XKeysymToKeycode`, `XSync`, `XSetErrorHandler`, `XFree`, `XCloseDisplay`, `XScreenSaverQueryExtension`,
  `XScreenSaverAllocInfo`, `XScreenSaverQueryInfo`, `XTestQueryExtension`, `XTestFakeKeyEvent`,
  `XTestFakeRelativeMotionEvent`; `src/Vigil.Platform.Linux/X11/X11NativeMethods.cs`, each with the header it was checked
  against), `ioctl` and `write` on `/dev/uinput` (`src/Vigil.Platform.Linux/Uinput`), and D-Bus through the one NuGet
  package Vigil uses for it, Tmds.DBus.Protocol, referenced only by `Vigil.Platform.Linux`
  (`src/Vigil.Platform.Linux/DBus/TmdsDBusConnection.cs` is the only file that uses it). Files it reads: the four
  attributes `type`, `online`, `status` and `scope` of `/sys/class/power_supply/*`, and (to tell whether its output is the
  systemd journal) the link of its own `/proc/self/fd/1` and `/proc/self/fd/2`. `grep -rn "XQueryPointer\|XGrabKey\|XSelectInput\|XRecord\|/dev/input/event" src/` finds nothing.
- Autostart: `LaunchAgentManager`, `RunKeyManager` and `SystemdUnitManager` are called only from `AutostartCommand`
  (`vigil install` and `vigil uninstall`).
- Files: everything Vigil writes is listed in [Files Vigil creates](#files-vigil-creates).

## Requirements and build from source

- macOS 12.0 or later (`LSMinimumSystemVersion` in the app bundle), or Windows 10 version 1809 or later (the oldest
  Windows 10 that .NET 10 supports and that has the Unix domain sockets Vigil uses, which arrived in version 1803), or
  64-bit Linux (x64 or arm64) with glibc, systemd for autostart, and, depending on the session: X11 with libX11, libXss
  and libXtst (`libx11-6`, `libxss1`, `libxtst6` on Debian and Ubuntu), or Wayland on GNOME with `xdg-desktop-portal` and
  the portal backend of your desktop (`xdg-desktop-portal-gnome`) at RemoteDesktop version 2 or newer. The release
  binary asks for the glibc version printed by the packaging script; older distributions may be too old.
- The .NET SDK version pinned in `global.json` (10.0.201, rolling forward to the latest feature band). Releases are
  built with Native AOT, so the platform's native toolchain for .NET Native AOT must be available (Xcode command line
  tools on macOS; on Windows the Visual Studio "Desktop development with C++" workload, plus the "C++ ARM64/ARM64EC
  build tools" component to build `win-arm64` on an x64 machine; on Linux `clang` and `zlib1g-dev`, and the architecture of
  the target: `linux-arm64` is built on an Arm machine).

```
git clone https://github.com/yasarbudak/vigil.git
cd vigil
scripts/package-macos.sh osx-x64      # Intel
scripts/package-macos.sh osx-arm64    # Apple silicon (not yet tested on hardware)
scripts/package-windows.ps1 win-x64   # Windows, Intel and AMD (PowerShell, on Windows)
scripts/package-windows.ps1 win-arm64 # Windows on Arm (not yet run anywhere)
scripts/package-linux.sh linux-x64    # Linux, Intel and AMD (on x86_64 Linux)
scripts/package-linux.sh linux-arm64  # Linux on Arm (on aarch64 Linux)
```

Each script builds the tray too (`src/Vigil.Tray`, Avalonia, also Native AOT), so the first build downloads the Avalonia
packages.

The macOS script publishes Native AOT binaries, assembles two app bundles, and signs each ad hoc: `Vigil.app` (the daemon and
the CLI) and `Vigil Tray.app` (the tray, with Avalonia's three native libraries next to its program), for example
`artifacts/osx-x64/Vigil.app` and `artifacts/osx-x64/Vigil Tray.app`, and zips both side by side as
`artifacts/<rid>/Vigil-<version>-<rid>.zip`. The tray is a bundle of its own so that `Vigil.app`, which holds the
Accessibility permission, stays exactly what it was: the same executable name `vigil`, identifier and contents. The Windows
script publishes three Native AOT programs, `vigil.exe` (the console program, every command), `vigild.exe` (the same daemon
without a console window, which autostart runs) and `vigil-tray.exe` (the tray, which needs Avalonia's `.dll` files and so
sits with them in a `tray` folder), and zips them: `artifacts/<rid>/vigil.exe`, `artifacts/<rid>/vigild.exe`,
`artifacts/<rid>/tray/` and `artifacts/<rid>/Vigil-<version>-<rid>.zip` (the zip also holds the `LICENSE` file, as
`Vigil.app/Contents/Resources/LICENSE` does on macOS). The Linux script publishes `vigil` and the tray and packs them with the
`LICENSE` file: `artifacts/<rid>/vigil`, `artifacts/<rid>/tray/` (`vigil-tray` with `libSkiaSharp.so` and `libHarfBuzzSharp.so`)
and `artifacts/<rid>/Vigil-<version>-<rid>.tar.gz`. They are not signed.

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

5. **Optional: the tray.** The zip also holds `Vigil Tray.app`, a menu bar icon with a status and settings window (see
   [Tray and settings window](#tray-and-settings-window)). It needs no permission. Copy it next to Vigil and open it:

   ```
   ditto "artifacts/osx-x64/Vigil Tray.app" "$HOME/Applications/Vigil Tray.app"
   open "$HOME/Applications/Vigil Tray.app"
   ```

   It is a separate app on purpose: it does not touch `Vigil.app`, its permission or its LaunchAgent. It shows no Dock icon.
   To start it at login, add it yourself under System Settings, General, Login Items (Vigil never does that). The Avalonia
   library the tray uses supports macOS 14 and later (its support policy puts macOS 13 and earlier in Tier 3, without free
   support); the bundle itself declares macOS 12 like `Vigil.app`, and it has been run only on macOS 15 so far.

### Updating

Every rebuild produces a new ad-hoc signature, and macOS then treats the app as a new one. After copying a new build
to `~/Applications`, remove the old Vigil entry in System Settings, Privacy & Security, Accessibility, add Vigil
again and switch it on (this concerns `Vigil.app` only; `Vigil Tray.app` has no permission, so replacing it asks for nothing). If the plist would change (for example you moved the app), run `vigil install --force` from
the new location. Check with `vigil diag` (via the bundle, as above) that `Post events` is `granted`.

## Install on Windows

Prebuilt, unsigned zips for `win-x64` and `win-arm64` will be attached to the
[Releases](https://github.com/yasarbudak/vigil/releases) page once a version is tagged. Until then, build from source
as described above. They are built and unit-tested in CI and have not yet been verified on a real Windows machine.

Vigil never starts itself. You can run `vigil run` in a terminal, or install autostart once with `vigil install` so
Windows starts it when you sign in. No step needs administrator rights, and Windows needs no permission for Vigil to
post its event.

1. **Put the folder somewhere permanent.** Unzip the release (or take `artifacts/win-x64`) and move the folder with
   `vigil.exe` and `vigild.exe` to a place that stays, for example `%LOCALAPPDATA%\Programs\Vigil`. Keep the two
   programs together: `vigil install` points autostart at the `vigild.exe` next to `vigil.exe`. The Run key limits
   the command line to 260 characters, so a very long folder path is refused with a message. The zip also holds a `tray`
   folder with `vigil-tray.exe` and its `.dll` files; keep that folder as it is, the program loads them from there.

2. **Install autostart.** From that folder:

   ```
   vigil.exe install
   ```

   It writes one value, `Vigil`, under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, whose
   data is the quoted path of `vigild.exe`, and creates the data directory and `vigil.log`. Windows then starts
   `vigild.exe` at your next sign-in (Windows may delay Run programs a little, and ignores them in Safe Mode). Like
   the macOS install, it also starts `vigild.exe` right away, without a window and with the same arguments as the
   value, unless a daemon already serves that data directory. Running it again with the same settings starts the
   daemon only if it is not running. With `--force`, a running daemon is stopped first and restarted with the new
   value; if it cannot be stopped, the old value stays. If the start fails, the value stays written, the command exits with 1 and says so.
   `vigild.exe` has no window: its output goes to `vigil.log`.

   `vigil install` refuses when `vigild.exe` is not next to `vigil.exe`, and when a different `Vigil` value already
   exists (another folder, another data directory) unless you pass `--force`. Running it again with the same folder and
   settings changes nothing. If `VIGIL_DATA_DIR` is set when you install, `vigild.exe` is started with
   `--data-dir "<that folder>"`, so the daemon and your CLI use the same directory.

3. **Verify.**

   ```
   vigil.exe status
   vigil.exe diag
   ```

   `Post events: granted` is always shown on Windows. `Screen locked:` should say `no` while you work (`unknown` is
   explained under [Screen lock detection (Windows)](#screen-lock-detection-windows)).

Add the folder to your `PATH` if you want to type `vigil` instead of the full path; the examples below write `vigil`.

**Optional: the tray.** Run `tray\vigil-tray.exe` for a notification area icon with a status and settings window (see
[Tray and settings window](#tray-and-settings-window)). It needs no permission and no administrator rights. To start it at sign-in, press Win+R, run `shell:startup`
and put a shortcut to `vigil-tray.exe` in that folder (Vigil never does that for you). Avalonia supports Windows 10 22H2 (x64)
and Windows 11; the earlier Windows 10 builds that Vigil itself supports are outside what the library supports.

### Updating

Replace `vigil.exe` and `vigild.exe` in the same folder (stop the daemon first with `vigil uninstall`, or Windows will
not let you overwrite `vigild.exe` while it runs, then run `vigil install` again). If you move the folder, run
`vigil install --force` from the new place. Windows needs no permission, so nothing has to be granted again.

## Install on Linux

Prebuilt, unsigned tarballs for `linux-x64` and `linux-arm64` will be attached to the
[Releases](https://github.com/yasarbudak/vigil/releases) page once a version is tagged. Until then, build from source
as described above. They are built and unit-tested in CI (the Arm build on a native Arm runner), CI starts each binary once
on a headless runner, and neither has been verified on a real desktop yet. If something does not work on
yours, `vigil diag` says which part cannot be reached.

Vigil never starts itself. You can run `vigil run` in a terminal inside your desktop session, or install a systemd user
unit once with `vigil install`. No step needs root, except the optional `uinput` setup, which is yours to do.

1. **Put the binary somewhere permanent.** Unpack the tarball and move `vigil` to a place that stays, for example
   `~/.local/bin/vigil` (and make sure that directory is on your `PATH`). `vigil install` points the unit at the
   absolute path of the binary you run it from.

   ```
   tar -xzf Vigil-<version>-linux-x64.tar.gz
   install -D Vigil/vigil ~/.local/bin/vigil
   ```

2. **Check the session.**

   ```
   vigil diag
   ```

   It shows the session type Vigil found (`XDG_SESSION_TYPE`, then `WAYLAND_DISPLAY`, then `DISPLAY`), where the idle time
   comes from, which input backend is selected, and whether the idle time can be read right now.

3. **X11 session.** Nothing to grant. Vigil needs `libX11.so.6`, `libXss.so.1` and `libXtst.so.6` (packages `libx11-6`,
   `libxss1`, `libxtst6`); if one is missing `vigil diag` names it, and the daemon does not crash.

4. **Wayland session (GNOME).** Input goes through the desktop portal's RemoteDesktop interface, which asks for your
   consent once. Run, from inside your session:

   ```
   vigil permissions request
   ```

   The daemon has to be running first (`vigil install` starts it, or use `vigil run`): the command does not ask the portal
   itself, it asks the running daemon to do it and waits for your answer (up to 6 minutes). Your desktop shows the dialog;
   allow it. The command prints whether the permission was granted, denied, cancelled or timed out and exits 0 only when it
   was granted. Without a daemon it says so and exits 1. The daemon keeps the session it just got, so it works at once, and
   Vigil saves the portal's restore token in `portal-restore-token` in the [data directory](#files-vigil-creates) (mode 0600);
   from then on the daemon starts its portal session with that token after every restart without a dialog. Until a consent
   is saved the daemon shows `Degraded (PermissionMissing)`, as macOS does without its permission. Only one request can wait
   at a time; a second one is answered with "already in progress".

   Why the daemon asks and not the command (not verified on a real desktop): the portal identifies an application by the
   systemd unit the process runs in (xdg-desktop-portal, `shared/xdp-app-info-host.c`: a process in a unit named `app-...`
   with a matching desktop file gets that application's identity, every other process gets none), and a saved consent only
   restores for the same identity. A command typed in a terminal usually runs in the terminal's own `app-...` scope, while
   the daemon runs in `vigil.service`, so a consent given from the terminal might not restore in the daemon. Because the
   daemon makes the request itself, the consent belongs to the identity that has to use it.

5. **Optional: the uinput fallback.** If your desktop has no portal with RemoteDesktop version 2 (or you do not want the
   dialog), Vigil can post through a virtual input device. This is something you set up yourself, with your own
   `sudo`; Vigil never asks for root, never installs the rule below, and uses uinput only when you select it with
   `"Linux": { "Backend": "Uinput" }` in `config.json`. Giving a user write access to `/dev/uinput` lets that user's programs
   create devices that type and move the pointer, so only do it on a machine where you accept that.

   ```
   sudo groupadd -r uinput
   sudo usermod -aG uinput "$USER"
   echo 'KERNEL=="uinput", SUBSYSTEM=="misc", GROUP="uinput", MODE="0660", OPTIONS+="static_node=uinput"' | sudo tee /etc/udev/rules.d/60-vigil-uinput.rules
   echo uinput | sudo tee /etc/modules-load.d/uinput.conf
   sudo modprobe uinput
   sudo udevadm control --reload && sudo udevadm trigger
   ```

   Then log out and in again so the group applies, and check `vigil diag` (`uinput: /dev/uinput is writable`). The rule
   is the usual udev form (`GROUP`, `MODE`, and `static_node` from udev(7)); adjust the group name to your policy.

6. **Install autostart.** From the binary you want to keep:

   ```
   vigil install
   ```

   It writes `~/.config/systemd/user/vigil.service` (`$XDG_CONFIG_HOME/systemd/user` if that is set; mode 0644, written
   atomically), runs `systemctl --user daemon-reload` and `systemctl --user enable --now vigil.service`, so Vigil also starts
   immediately. The unit is part of your graphical session (`WantedBy=`, `PartOf=` and `After=graphical-session.target`),
   starts `<binary> run`, and restarts it only after a crash (`Restart=on-abnormal`, 30 seconds apart): a deliberate exit,
   such as an invalid `config.json` (exit code 2), is not restarted, so there is no restart loop. `vigil install` creates
   the data directory; the log goes to the journal (see [Logs](#logs)).

   `vigil install` refuses when there is no systemd user instance (it says so and writes nothing), and when a different
   Vigil unit already exists (another binary location, another data directory) unless you pass `--force`. Running it again
   from the same binary with the same settings changes nothing. If `VIGIL_DATA_DIR` is set when you install, the same
   value is stored in the unit so that the daemon and your CLI use the same directory.

7. **Verify.**

   ```
   vigil status
   vigil diag
   systemctl --user status vigil
   journalctl --user -u vigil
   ```

8. **Optional: the tray.** The tarball also holds `Vigil/tray/` with `vigil-tray` and the two libraries next to it (keep them
   together; the program loads them from its own folder). Install the folder as a whole and start it from inside your session:

   ```
   cp -r Vigil/tray ~/.local/share/vigil-tray-app
   ~/.local/share/vigil-tray-app/vigil-tray
   ```

   It needs no permission. It needs the packages `libx11-6 libice6 libsm6 libfontconfig1` (Debian and Ubuntu names; Avalonia's
   list), an X11 display (`DISPLAY`: on a Wayland session the tray runs through XWayland, because the Avalonia package used here
   only has the X11 backend), and a desktop that shows tray icons: see [Tray and settings window](#tray-and-settings-window).
   To start it at login, add it to your desktop's startup applications (Vigil never does that for you).

### Updating

Replace the binary, then run `vigil install --force` (it stops the running daemon, rewrites the unit and starts it again).
If you only replaced the file at the same path, `systemctl --user restart vigil` is enough. The portal consent and the
saved token stay valid; nothing has to be granted again.

## CLI reference

```
vigil run                      Run the daemon in the foreground. Stop it with Ctrl+C.
vigil status                   Show what the running daemon is doing.
vigil pause <duration>         Pause Vigil for a while, e.g. 30s, 15m, 1h, 1h30m.
vigil pause --until-tomorrow   Pause until the start of tomorrow's schedule window.
vigil resume                   End a manual pause.
vigil diag                     Print platform, idle time, permission and configuration.
vigil permissions request      Ask for permission to post input events (macOS; Linux Wayland: the portal consent, asked by the running daemon). Windows and X11 need none.
vigil install [--force]        Start Vigil at login (macOS LaunchAgent, Windows Run key, Linux systemd user unit).
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
- `permissions request` is the only command that can make the OS show a permission prompt (macOS: the Accessibility
  prompt; Linux Wayland: the portal's RemoteDesktop consent dialog, which the running daemon shows and the command waits
  for, up to 6 minutes; with no daemon running it prints how to start one and exits 1). It exits 0 if
  the permission is granted and 1 if not, with the reason (denied, cancelled, timed out, already in progress). On Windows it prints that there is nothing to request and
  exits 0; on Linux X11 and uinput it only reports whether posting is possible.
- `install` does only what is described under [Install on macOS](#install-on-macos) (run it from inside
  `Vigil.app`), [Install on Windows](#install-on-windows) (run it from the folder with `vigil.exe` and
  `vigild.exe`) or [Install on Linux](#install-on-linux). `--force` replaces an existing autostart entry that differs from
  what would be written.
- `uninstall` is described under [Uninstall](#uninstall).

On platforms other than macOS, Windows and Linux, `install` and `uninstall` exit 1 with "not supported yet".

### Sample status output

```
State:           Watching
Last activity:   09:37:42 (0s ago)
Last injection:  none yet
Today:           0 injections, 0s covered
Screen lock:     detected, pausing while locked
Platform:        X11 session, idle time from the X server (MIT-SCREEN-SAVER), input through X11 (XTest)
```

The `Platform` line is shown on Linux only: it names the session type, where the idle time comes from and the input
backend. `vigil diag` prints the same facts, line by line, plus whether each can be used right now.

`State` shows the reason for `Paused` (`Paused (Manual)`, `Paused (OutsideSchedule)`, ...) and for `Degraded`. A
manual pause shows its end time, for example `Paused (until 10:15:00)`. `Screen lock` is one of
`detected, pausing while locked`, `UNKNOWN: cannot tell if the screen is locked, NOT pausing while locked`,
`not used (PauseOnLock is off)` or `not checked yet`.

## Tray and settings window

`vigil-tray` is an optional program with an icon in the menu bar (macOS), the notification area (Windows) or the panel of your
desktop (Linux), and a window with the status and the settings. The daemon works without it, and it needs the daemon only to
show the status: it is a client that talks to the daemon over the local socket (the same one `vigil status` uses) and reads and
writes `config.json`. It never posts input, never reads the idle time, needs no permission, uses no network, and never starts,
installs or stops the daemon. It starts at login only if you set that up yourself (see the install sections above); Vigil
installs no login item for it.

**The icon** shows the state: a ring with a dot (`Watching`), a solid disc (`Injected`), two bars (`Paused`), an exclamation mark
(`Degraded`) and a dashed ring (the daemon is not running). The icons are drawn by the program itself, so there are no image files
and nothing from a third party. On macOS they are black template images that the menu bar tints for light and dark mode; on
Windows and Linux each state is a coloured disc with a white shape, which reads on a dark or a light panel. The tooltip names the
state like `vigil status` does, for example `Vigil: Paused (until 10:15:00)`.

**The menu** shows the state line and `Today: N injections, X covered` (both worded as `vigil status` words them), then
`Pause 15 minutes`, `Pause 1 hour`, `Pause until tomorrow` (the same commands as `vigil pause`), `Resume` (enabled while a manual
pause is on; the schedule, lock and battery pauses end by themselves), `Open Vigil…` (the window) and `Quit tray`. **Quit tray
closes only the tray: the Vigil daemon keeps running** (the menu item says so in its tooltip). When no daemon is running the
icon is the dashed ring, the pause entries are disabled, and the window still opens, so you can edit the settings.

**The window** shows what `vigil status` prints, refreshed every two seconds while the window is open, and the documented
settings: `Threshold`, `PauseOnLock`, `PauseOnBattery`, the schedule (days, start, end), the activity log (`Enabled`,
`RetentionDays`) and the injection strategy of this operating system. Save checks the values with the daemon's own loader before
anything is written, so the rules and the messages are the daemon's (a bad value shows the same message `vigil diag` would show,
and nothing is written). The file is written atomically (a finished temporary file is renamed over it) with mode 0600 on Unix.
Keys the window does not show (`PollInterval`, `VerifyDelay`, `DegradedRetryInterval`, `Linux.Backend`, the sections of other
operating systems, any key Vigil does not know) are kept as they are. Comments in the file are not kept, because the writer has
no place for them. A file that is not valid JSON is never overwritten: the window says what is wrong and asks you to fix it.

**Applying the settings.** After a successful save the tray asks the running daemon to read `config.json` again (the `ReloadConfig`
IPC command). The daemon validates the file first; if it is not valid, it changes nothing and the window shows its error. If it is
valid the daemon applies it without a restart: all `Vigil.*` keys (a new `PollInterval` once the current wait is over), the activity log
(a shorter `RetentionDays` deletes old files at the next daemon start or at midnight, as always, not at once) and the strategy of
this operating system. Two things cannot be taken over by a running daemon, and the window names them: `Linux.Backend` (the input
backend is built once at start) and, on the `Uinput` backend, `Linux.Strategy` (the virtual device is made for one strategy). They
are saved and apply after you restart the daemon. If the daemon is not running, the settings apply when it starts. `config.json`
stays the only source of configuration.

**Desktop requirements.**

- macOS: nothing to set up. The icon appears in the menu bar. Avalonia supports macOS 14 and later.
- Windows: nothing to set up. Windows 10 22H2 (x64) and Windows 11 are what Avalonia supports.
- Linux: the tray uses the StatusNotifierItem D-Bus interface: it connects to the session bus, watches for
  `org.kde.StatusNotifierWatcher` and shows its icon once a watcher exists. It falls back to the older XEmbed system tray only when
  it cannot connect to the session bus at all (Avalonia's `DBusTrayIconImpl` and `X11Platform.CreateTrayIcon`). Avalonia's
  documentation says the icon works on desktops with StatusNotifierItem or AppIndicator support (confirmed on Ubuntu) and that
  "GNOME users may need the AppIndicator extension": plain GNOME shows no tray icons, so install and enable that extension, or the
  icon does not appear. Which desktops show it has not been tried here.

A tray that is already running is not started a second time (the lock file `vigil-tray.lock` in the data directory); the second
start says so and exits.

## Configuration

Vigil reads an optional `config.json` from the data directory (macOS: `~/Library/Application Support/Vigil/`, Windows:
`%LOCALAPPDATA%\Vigil\`, Linux: `$XDG_DATA_HOME/vigil/`, by default `~/.local/share/vigil/`, or `$VIGIL_DATA_DIR/config.json`). A missing file means defaults. It is the only configuration source: environment
variables and command-line options do not configure the daemon. The daemon reads it at start; to change it while the daemon runs,
use the [tray's settings window](#tray-and-settings-window) (it saves the file and asks the daemon to reload it), or restart the
daemon after editing the file by hand.

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
  },
  "Windows": {
    "Strategy": "MouseNudge"
  },
  "Linux": {
    "Strategy": "MouseNudge",
    "Backend": "Auto"
  }
}
```

All three platform sections are read on every platform (a bad value in any of them stops the daemon), but only the one
for the running OS has an effect.

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
| `MacOS.Strategy` | `MouseNudge` or `F15KeyUp` | `MouseNudge` | Which invisible event is posted on macOS. |
| `Windows.Strategy` | `MouseNudge` or `F15KeyUp` | `MouseNudge` | Which invisible event is posted on Windows. Whether Windows counts a zero-pixel mouse move as input has not been checked on a real machine; if the state shows `Degraded (VerificationFailed)`, set this to `F15KeyUp`. |
| `Linux.Strategy` | `MouseNudge` or `F15KeyUp` | `MouseNudge` | Which invisible event is posted on Linux. Which one the X server or your compositor counts as input has not been checked on a real desktop; if the state shows `Degraded (VerificationFailed)`, try the other one. |
| `Linux.Backend` | `Auto`, `X11`, `Portal` or `Uinput` | `Auto` | How input is posted (the idle time and lock state always follow the session type). `Auto`: XTest in an X11 session, the desktop portal in a Wayland session, never uinput. `X11` forces XTest (on Wayland it only reaches XWayland and `vigil diag` warns). `Portal` forces the portal. `Uinput` is the opt-in fallback described under [Install on Linux](#install-on-linux). |

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
- Windows: `%LOCALAPPDATA%\Vigil\` (usually `C:\Users\<you>\AppData\Local\Vigil\`)
- Linux: `$XDG_DATA_HOME/vigil/`, by default `~/.local/share/vigil/` (a relative `XDG_DATA_HOME` is ignored, as the
  [XDG Base Directory Specification](https://specifications.freedesktop.org/basedir-spec/latest/) says). The data base is
  used, not the state base, because the directory holds your own `config.json` next to the logs and counters.
- Override: set the `VIGIL_DATA_DIR` environment variable. Use it, for example, to try a configuration without
  touching the real directory. If it is set when you run `vigil install`, the value is stored in the LaunchAgent
  (macOS), in the systemd unit (Linux) or passed to `vigild.exe` as `--data-dir` (Windows).

| Path | Purpose | Unix mode | Windows |
| --- | --- | --- | --- |
| the data directory | Holds everything below. | 0700 | access rules of its parent |
| `config.json` | Your optional configuration. The daemon and the CLI never write it; only the tray's Save does (atomically, other keys kept). | as you create it, 0600 after a save from the tray | as you create it |
| `vigil.sock` | Local Unix domain socket the CLI uses to talk to the daemon. | 0600 | access rules of the data directory |
| `vigil.sock.lock` | Lock file that guarantees a single daemon instance. | 0600 | access rules of the data directory |
| `vigil-tray.lock` | Empty lock file that keeps a second tray from starting. Created only when the tray runs. | 0600 | access rules of the data directory |
| `activity-YYYY-MM-DD.jsonl` | Daily activity record: one JSON line per event with timestamps and counters, no content. Deleted after `RetentionDays`. Not written when `ActivityLog.Enabled` is `false`. | 0600 | access rules of the data directory |
| `vigil.log`, `vigil.log.1` | Daemon log and its one backup. See [Logs](#logs). Not written when the daemon runs under systemd on Linux: the journal keeps the log. | 0600 | access rules of the data directory |
| `portal-restore-token` | Linux Wayland only: the desktop portal's restore token, one line of letters and digits, saved by the daemon when you run `vigil permissions request`, and renewed each time the daemon starts a session (the portal's tokens are single use). It lets the daemon restart without showing the consent dialog. Deleted when the portal does not accept it. | 0600 | not used |

On Windows Vigil sets no access rules of its own: the data directory inherits them from its parent, and the default
`%LOCALAPPDATA%` is inside your user profile, which other standard users cannot open by default (not yet checked on a
real machine). A `VIGIL_DATA_DIR` outside your profile is only as private as its parent folder, so keep it inside the
profile. Unix domain sockets on Windows also have a path limit of 108 bytes; Vigil refuses a longer socket path.

Outside the data directory, `vigil install` writes
`~/Library/LaunchAgents/io.github.yasarbudak.vigil.plist` on macOS, the value `Vigil` under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` on Windows, or
`~/.config/systemd/user/vigil.service` (`$XDG_CONFIG_HOME/systemd/user/vigil.service`) on Linux, and nothing else does.
On Linux the consent itself is stored by the desktop portal, not by Vigil; the token file above is only the key to it.

## Logs

The daemon always writes its log to `vigil.log` in the data directory, whether started by `vigil run`, by the
LaunchAgent or (Windows) by `vigild.exe`, which has no console; `vigil run` also prints to the terminal. The file is capped at 1 MiB: when a line would go over,
`vigil.log` becomes `vigil.log.1` (replacing the previous one) and a new `vigil.log` starts. At most one backup is
kept, so logs use about 2 MiB of disk. A `vigil.log` that is already over the cap at daemon start is rotated first.
Both files are 0600 on Unix. Startup errors (such as an invalid `config.json`) and unhandled exceptions are written
there too. Follow it with:

```
tail -f ~/Library/Application\ Support/Vigil/vigil.log          # macOS
Get-Content -Wait "$env:LOCALAPPDATA\Vigil\vigil.log"           # Windows PowerShell
```

Under systemd on Linux the log is the journal's, not a file: when the daemon's output is connected to the journal
(Vigil checks `$JOURNAL_STREAM` against its own standard output and error, as systemd.exec(5) describes), it writes to the
console only, in the format journald reads, so `journalctl -p warning` filters it, and journald timestamps, rotates and
caps it. Read it with:

```
journalctl --user -u vigil            # add -f to follow
```

A `vigil run` in a terminal still writes `vigil.log` as before.

On Windows the running daemon keeps `vigil.log` open for writing, so a program that reads it must open it with
read and write sharing (a plain .NET `File.ReadAllText` does not, and fails while the daemon runs). The daemon renames
the file when it rotates it, and Windows refuses a rename while another program holds the file open without sharing
delete access. In that case Vigil copies the log to `vigil.log.1` and empties `vigil.log` in place instead, and a program
following the file sees it shrink, as `tail -f` does.

## Uninstall

```
~/Applications/Vigil.app/Contents/MacOS/vigil uninstall            # stop Vigil and remove the LaunchAgent
~/Applications/Vigil.app/Contents/MacOS/vigil uninstall --purge    # ... and also delete the data directory
```

On Windows run the same commands as `vigil.exe uninstall` and `vigil.exe uninstall --purge`. On Linux run
`vigil uninstall` and `vigil uninstall --purge`.

- `vigil uninstall` on macOS unloads the LaunchAgent (stopping the daemon it started) and deletes the plist. If
  nothing is installed it says so and does nothing. The data directory is kept.
- `vigil uninstall` on Windows asks the running daemon to stop, over the same local socket the CLI uses (the only
  reason that socket accepts a stop request), waits until it is gone (up to 15 seconds), and deletes the `Vigil` Run
  value. It stops whichever daemon serves the data directory, also one you started by hand with `vigil run` or
  `vigild.exe`. If the daemon cannot be stopped, the Run value is kept and the command exits 1. The data directory
  is kept.
- `vigil uninstall` on Linux runs `systemctl --user disable --now vigil.service` (which stops the daemon the unit
  started and removes the unit's links), deletes the unit file and runs `daemon-reload`. If the unit cannot be stopped, the file
  is kept and the command exits 1. It does not stop a daemon you started by hand with `vigil run`. The data directory
  is kept; the portal consent also stays at the portal (remove it in your desktop's privacy settings if you want it gone).
- `--purge` additionally deletes the default data directory (`~/Library/Application Support/Vigil` on macOS,
  `%LOCALAPPDATA%\Vigil` on Windows, `~/.local/share/vigil` or `$XDG_DATA_HOME/vigil` on Linux), including your `config.json`, the activity log and the daemon log. It deletes
  only that default directory: if `VIGIL_DATA_DIR` points somewhere else, or the directory is a symbolic link (on
  Windows also a junction), it refuses and tells you so. Stop any `vigil run` you started by hand first (on Windows
  `vigil uninstall` does that).

To remove Vigil completely:

1. Run `vigil uninstall --purge` as above.
2. macOS: delete `~/Applications/Vigil.app` and remove Vigil from System Settings, Privacy & Security, Accessibility.
   Windows: delete the folder with `vigil.exe` and `vigild.exe`. Linux: delete the `vigil` binary.
3. If you use the tray: quit it (`Quit tray` in its menu), remove the login item or startup shortcut you added for it, and delete
   `Vigil Tray.app` (macOS), the `tray` folder (Windows) or the folder you copied the tray to (Linux). `vigil uninstall` does
   not stop or remove the tray, because it never started it.

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
(`launchctl bootout` through `vigil uninstall`, or Ctrl+C for a foreground `vigil run`; on Windows `vigil uninstall`
stops it too) before starting another. This exits with code 1. On Windows the second daemon cannot write that line to
`vigil.log`, because the first one has the file open for writing; the message appears on its console only.

**`The Vigil daemon is not running`.** `status`, `pause` and `resume` need a running daemon. Start it with
`vigil run`, or `vigil install` for autostart.

**Configuration errors (exit code 2).** The message names the file and each violated rule. Fix `config.json` and
start the daemon again. `vigil diag` validates the file without starting the daemon.

**`Screen lock: UNKNOWN`.** Vigil could not read the lock state and keeps injecting; see
[Screen lock detection](#screen-lock-detection-macos).

**`vigil install` refuses to run.** On macOS run it from inside the bundle
(`.../Vigil.app/Contents/MacOS/vigil install`); on Windows run it from the folder that holds both `vigil.exe` and
`vigild.exe`, in a folder whose path is short enough for the Run key (260 characters). Use `--force` if a different
Vigil entry exists.

**Windows: `Degraded (VerificationFailed)` with the default `MouseNudge`.** The zero-pixel mouse move was posted but
the idle counter did not reset. Set `Windows.Strategy` to `F15KeyUp` in `config.json` and restart the daemon.

**Windows: `Degraded (InjectionFailed)`.** `SendInput` inserted nothing. The usual cause is UIPI: a program running with
higher rights than Vigil (an elevated window) has the focus, and Windows blocks input into it without saying why. It
recovers once you leave that window. The reason text in `vigil.log` repeats this.

**Windows: Vigil does not start at sign-in.** Check that the `Vigil` value exists under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` and that the path in it still points at an existing
`vigild.exe`. Windows can delay Run programs a little after sign-in and ignores them in Safe Mode. Then read
`vigil.log`: a `vigild.exe` that starts and then stops (an invalid `config.json`, a second instance) writes the
reason there, because it has no console.

**Linux: `vigil diag` says `Idle time: unavailable`.** The message names the cause. No display or session type
(`XDG_SESSION_TYPE`, `WAYLAND_DISPLAY` and `DISPLAY` are all unset): you are outside the desktop session (for example in
an SSH login), or the systemd user manager does not have the session's environment. From inside the session run
`systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XDG_SESSION_TYPE` (desktop sessions normally do this at
start; not verified here) and then `systemctl --user restart vigil`. A daemon without a session in its environment
exits with this message (exit code 1, not restarted). A missing library: install the package it names. On Wayland
without GNOME: there is no idle source Vigil can read, see [Idle time and battery (Linux)](#idle-time-and-battery-linux).

**Linux: `Degraded (PermissionMissing)` on Wayland.** There is no saved portal consent (`vigil diag` shows
`Portal consent: none saved`) or the portal did not accept the saved one and Vigil removed it. Run
`vigil permissions request` and allow the dialog. If you did and it comes back after a restart, the portal did not accept
the saved consent for the daemon; run the request again (it is always made by the daemon itself, see the note on application
identity under [Install on Linux](#install-on-linux), step 4) and check what `vigil diag` shows (`Portal consent`, `Portal session`). If the request says the portal returned no restore token, the backend of your desktop does not
support persistent permissions: use the `uinput` backend instead. If it says the portal is not on the session bus or has no
RemoteDesktop interface, install `xdg-desktop-portal` and your desktop's backend.

**Linux: `Degraded (VerificationFailed)` or `(InjectionFailed)`.** The event was posted (or refused) but the idle counter
did not reset. Try the other `Linux.Strategy`. `vigil diag` shows the backend in use; the daemon's reason is in the journal.
On the `uinput` backend the first injection waits a second after creating the virtual device (the kernel documentation says
userspace needs a moment to notice a new device), and a desktop that ignores a device with only a few keys or buttons will not
count its events: that shows up as `VerificationFailed`.

**Linux: `vigil install` says there is no systemd user instance.** Autostart on Linux is a systemd user unit, so it needs
`systemctl --user` to work in your session. On a system without systemd, start `vigil run` from your session's own
autostart mechanism.

**Linux: the screen lock is not detected.** `Screen locked: unknown` means logind could not be read. `Screen locked: no`
while the screen is locked means your desktop does not set the logind locked hint; see
[Screen lock detection (Linux)](#screen-lock-detection-linux).

**The tray shows a dashed ring and "Vigil is not running".** There is no daemon at the socket. The tray does not start it:
run `vigil run` or `vigil install`. If the daemon runs and the tray still shows this, it is not using the same data directory:
check that `VIGIL_DATA_DIR` is the same for both.

**Linux: the tray has no icon.** Your desktop shows no tray icons. On GNOME install and enable the AppIndicator extension; on a
window manager without a panel there is nothing to show it. The tray prints nothing about this; run it from a terminal to see
Avalonia's messages. It also needs `DISPLAY` (XWayland on a Wayland session).

**Saving settings says the daemon cannot be reached, or "Restart the Vigil daemon to apply".** The file is saved either way.
Without a daemon the settings apply at its next start; the keys the message names (`Linux.Backend`, and `Linux.Strategy` on the
`Uinput` backend) apply after you restart the daemon.

**Windows: SmartScreen warns about `vigil.exe`.** The programs are not signed. Check the zip against `SHA256SUMS` from
the release before you allow it.

## Roadmap

- Verify the Windows build on real machines (x64 first, then Arm). Today it builds and passes its unit tests in CI.
- Verify the Linux build on real desktops (X11 first, then GNOME on Wayland), measure which strategy each counts as input,
  and confirm the portal consent survives a restart under the systemd unit.
- Idle time on Wayland beyond GNOME (the `ext-idle-notify-v1` protocol, KDE Plasma, wlroots compositors).
- Start the tray at login (today you add it to your login items, startup folder or desktop autostart yourself).
- Verify the tray on real desktops (macOS menu bar, Windows notification area, GNOME with the AppIndicator extension, KDE Plasma).

## Contributing and development

Developer documentation, including the architecture and the rules that protect the trust guarantees above, is in
[docs/DEVELOPER.md](docs/DEVELOPER.md). To build and run the tests:

```
dotnet build -c Release
dotnet test
```

The tests use a fake platform, so they never post real input events.

Please do not run `vigil run`, `vigil permissions request`, `vigil install` or `vigil uninstall` on a development
machine just to see them work: they post input events, may show a system prompt, or change your LaunchAgents, Run
key or systemd units. `vigil diag` is read only.

## License

MIT. See [LICENSE](LICENSE). The programs contain third-party components (the .NET runtime, Avalonia, SkiaSharp and Skia,
HarfBuzz, ANGLE, Tmds.DBus.Protocol and others), whose licenses are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt); it is
shipped next to `LICENSE` in every package (in `Contents/Resources` of both macOS apps).
