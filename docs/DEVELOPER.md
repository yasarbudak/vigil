# Developer guide

## Working principle: no vibecoding

AI tools may be used as a pair partner. The developer leads, the tool produces, the developer verifies.
Every line must be understood, explainable and defensible by the person who commits it.

## Architecture

```
src/Vigil.Core              platform-free logic and abstractions (AOT compatible, no I/O)
src/Vigil.Platform.MacOS    macOS implementations (LibraryImport P/Invoke to CoreGraphics, IOKit, CoreFoundation),
                            and the pure/OS-facing LaunchAgent pieces (plist, bundle check, launchctl)
src/Vigil.Platform.Windows  Windows implementations (LibraryImport P/Invoke to user32, kernel32, wtsapi32), the pure
                            tick-count, session-flag and power-status logic, and the Run key behind `IRunKey`
src/Vigil.Platform.Linux    Linux implementations: X11 (libX11, libXss, libXtst, loaded by name), the desktop portal, the
                            GNOME idle monitor and logind over D-Bus (the one NuGet package, Tmds.DBus.Protocol, used in
                            one file), uinput, sysfs battery, the systemd user unit and `ISystemctl`
src/Vigil.App               the application: CLI, daemon host, IPC, config, activity log, install, platform
                            composition. A library; its entry points are `VigilApplication.RunCliAsync/RunDaemonAsync`.
                            `Tray/` holds the tray's logic without any UI library (models, IPC client, settings store, tests
                            need no Avalonia); `vigil-tray` sees it through `InternalsVisibleTo`
src/Vigil                   `vigil` (macOS and Linux binary, `vigil.exe` on Windows): console head over Vigil.App, all commands
src/Vigild                  `vigild.exe`: windowless (WinExe) head over Vigil.App that only runs the daemon (Windows)
src/Vigil.Tray              `vigil-tray` (`vigil-tray.exe` on Windows, WinExe): the tray icon, its menu and the settings window,
                            written with Avalonia in code (no XAML, no bindings). The only project that references Avalonia;
                            it holds placement only, every decision is in `src/Vigil.App/Tray`
tests/Vigil.Core.Tests      engine, schedule, options, duration parser (hand-written fakes, no mocking library)
tests/Vigil.Tests           app and platform layer (including the tray's logic): IPC server/client, activity log, config binding, config reload, settings store, daemon host with a
                            fake platform, Windows pure logic and install flow with a fake registry, Linux pure logic
                            (classifiers, D-Bus messages, the portal flow against a fake portal, unit rendering, install
                            flow with a fake systemctl)
```

Why two heads over a library: the Windows daemon must not open a console window, and that is a property of the
executable (its PE subsystem, which `OutputType` WinExe selects and Native AOT passes on to the linker as
`/SUBSYSTEM:WINDOWS`), not of the code. So there are two executables, `vigil.exe` (console, every command including
a foreground `run`) and `vigild.exe` (GUI subsystem, no console). Both need the same code; one executable referencing the
other would tie its build and publish to the other's, and sharing the files by linking or copying would duplicate the
logic. The usual pattern is the one used here: every line of logic lives in the `Vigil.App` library and each executable
is a `Program.cs` of a few lines. Only
`VigilApplication` is public; the rest stays `internal`, and the tests see it through `InternalsVisibleTo`.
`<Version>` is defined once, in `Directory.Build.props`, so `vigil`, `vigild` and the packages agree. macOS and Linux ship
only `vigil`; `Vigild` builds on every OS (it is managed code until published) and is only published on Windows.

Layer responsibilities:

- **Core** decides. `PresenceEngine` is a deterministic state machine fed with observations and injection
  results; all time comes from `TimeProvider`. `PresenceCycle` is one poll step (observe, decide, inject, wait,
  re-read idle, report). Core talks to the world only through `IIdleSource`, `IInputInjector`, `ISessionState`,
  `IPermissionProbe`, `IPermissionRequester` and `IActivityLog`.
- **Platform.\*** implements those interfaces for one OS and nothing more. Each P/Invoke carries a comment naming
  the SDK header (macOS), the Microsoft Learn page (Windows) or the X.org and kernel header and documentation page (Linux)
  it was verified against. CoreFoundation objects are
  owned by `CFTypeHandle` (SafeHandle). The one native buffer on Windows (`WTSQuerySessionInformationW`) is freed in a
  `finally` with `WTSFreeMemory`.
- **Vigil.App** wires everything: `PlatformServices` (the only OS switch), `DaemonHost` (composition root),
  `PresenceWorker` (poll loop on `PeriodicTimer`), `IpcServer`/`IpcClient` (newline-delimited JSON over a Unix
  domain socket), `VigilPaths` (the only place that knows file locations), `JsonlActivityLog` (with
  `ActivityLogRetention` for the retention cleanup), `Logging/` (the rotating `vigil.log`), `LaunchAgentManager` and
  `RunKeyManager` (install/uninstall), commands.

Single instance and IPC: `IpcServer` holds an exclusive lock on `vigil.sock.lock` for the daemon's lifetime. Only
the lock holder may delete or recreate the socket, and a second daemon fails at the lock. The IPC server is
registered before `PresenceWorker`, so a second daemon fails before anything can inject or write the activity log.
The server also makes its directory 0700 itself before binding, serves clients concurrently (limit 8), and never
lets an accept or handler error end the loop. The 5 s limit of a connection covers only reading the request and writing the
response (both through `CreateTimeLimit`), not the handler: `IIpcHandler.HandleAsync` runs under the daemon's stopping token only,
because `IpcCommand.RequestPermission` waits for a person; its handler bounds the wait itself (`IpcTimeouts`: the client waits 6 min
for that one command and 5 s for every other, the daemon ends a pending request after 5 min 30 s, so its answer always comes first).
A pending request holds one of the 8 slots, and the server watches its connection (`HandleWatchingClientAsync`, only for commands
where `IpcTimeouts.IsLongRunning`): the client sends nothing after its request line, so a read that completes with zero bytes means the
peer closed it (`Socket.Receive` documentation: after a remote shutdown the call "will complete immediately and return zero bytes"), and
a broken connection fails the read. Either cancels the handler's token, and the portal request is closed (`PortalClient.RequestAsync`
closes the Request on cancellation), so a Ctrl+C on the CLI removes the dialog and frees the slot. The daemon logs the request and its
outcome in `IpcHandler` (Information when granted, Warning otherwise; never the token).

Logging: `RotatingFileLoggerProvider` (one `RotatingFileWriter` shared by all loggers) writes `vigil.log` in the
console's single-line layout, next to the console logger. A line that would take the file over 1 MiB first moves it
to `vigil.log.1` (replacing it); an oversized file found at start is rotated first. The writer holds a lock, flushes
every line and never throws: an I/O failure drops the line and the file is reopened on the next write. The LaunchAgent
sets no `StandardOutPath` or `StandardErrorPath`, because a second writer (launchd) on the same file would break
rotation; stdout and stderr are discarded under launchd. So everything that ends a run must also reach the logger:
Under systemd (the output is the journal, see "Linux notes") there is no file and the console messages are the record.
`DaemonHost.RunAsync` logs the config error (exit 2), a failed start such as a second instance (exit 1), unexpected
exceptions (an exception filter that does not catch) and `AppDomain.UnhandledException`. Native crashes are covered by
macOS crash reports, not by this log. Tests: `RotatingFileLogTests`.

Configuration: `VigilConfiguration` is the single place that reads and binds `config.json` (`Load`, `LoadValidated`).
The daemon validates before building the host; `diag` and `permissions request` use the same loader. A bad file
raises `ConfigurationException`, which the commands print without a stack trace (exit code 2). A wrong value type is reported
with the binder's message for the key and the conversion error (`Failed to convert configuration value at 'Vigil:Threshold' ... String 'abc'
was not recognized as a valid TimeSpan.`), so that the tray's window can show something that leads to the mistake.
`LoadFromJson` binds text exactly as `Load` binds the file (same code path), which the tray uses to check what it is about to write.

**Reloading** (`IpcCommand.ReloadConfig`, `Configuration/ConfigReloader`): the daemon used to read `config.json` once, at start, and
copy values into objects it shared (the options record into the engine and the cycle, a `PeriodicTimer` interval, the activity log
chosen by `Enabled`, platform options into the injectors). A reload re-reads it with `VigilConfiguration.LoadValidated` (the loader the
start uses), and only a valid file is applied; an invalid one changes nothing and its message goes back in `IpcResponse.Error`. Applied in
place: `PresenceEngine.Reconfigure` swaps the whole `VigilOptions` instance under the engine's lock (state, counters and a manual pause stay;
the instance is never modified after the hand-over), `PresenceCycle` and `PresenceWorker` read `PresenceEngine.Options` at each poll
(the worker sets `PeriodicTimer.Period` when `PollInterval` changed), `ReloadableActivityLog` forwards to a new implementation when
`ActivityLog` changed (retention applies at the next scheduled prune, which stays once per day: a reload does not delete files), and the
injection strategy of macOS and Windows (and Linux, see below) is assigned to the options object the injectors read at every injection.
Not applied, reported in `IpcResponse.RestartRequired`: `Linux.Backend` (`LinuxPlatform.Create` builds the backend, a portal session or a
uinput device, once) and `Linux.Strategy` while the running backend is `Uinput` (`UinputDevice` is created with the capabilities of one
strategy; assigning the new one would make `Write` post an event the device does not declare, which the input core drops, so the
injection would be "posted" and never verified). Those two keep their running value, so the daemon and what it reports stay consistent, and
`config.json` has the new value for the next start. Why not restart the host in place: a failed rebuild would end the daemon, the portal
session and the manual pause would be lost, and every new key would silently depend on the rebuild; the per-key list above is short and
`ConfigReloaderTests` pins it. A key added later that the daemon copies at start must be added to the reloader or to the restart list.

Presence rules: a verified injection means idle time read after `VerifyDelay` is below `VerifyDelay` + 1 s.
Otherwise the state becomes `Degraded` and is retried every `DegradedRetryInterval`. A later verified injection
recovers automatically. Covered time excludes pauses, closed schedule windows, locked screens and suspend/sleep.
Covered time is measured from the latest of the last real activity, the previous injection and the start of the
current active stretch; the stretch starts when the engine is created (daemon start), so idle time before the daemon
ran is never counted. Covered time counts only periods in which injections were verified: every failure
(`EnterDegraded`: failed post, failed verification, missing permission) records `_lastUnverifiedAt`, and the first
verified injection after it covers nothing unless real user activity happened after the failure. Without this, a
recovery after a Degraded stretch would have credited the whole stretch (`PresenceCycleTests`,
`PresenceEngineTests`).
Schedules are wall-clock windows, so DST transitions follow the clock on the wall (see `ScheduleDstTests`). Pause reasons are evaluated in the order manual, schedule, lock, battery.

Screen lock on macOS: `MacOSSessionState.IsScreenLocked` reads `CGSessionCopyCurrentDictionary` and hands the
three relevant values (public `kCGSSessionOnConsoleKey` and `kCGSessionLoginDoneKey`, private
`CGSSessionScreenIsLocked`) to `SessionLockClassifier`, a pure function covered by `SessionLockClassifierTests`.
The private key is defined in Apple's open-source xnu (`iokit/IOKit/IOKitKeysPrivate.h`,
`kIOConsoleSessionScreenIsLockedKey`) and is not in the SDK headers, so the classifier returns unknown (`null`)
whenever the dictionary does not contain a public session key or a value has an unexpected type, instead of
reading a missing key as "unlocked". `StartupWarnings` holds the warning texts and warns only in that unknown case.
The engine reports each poll's "could the lock state be read" in `PresenceStatus.LockStateKnown` (with
`PauseOnLock`), `vigil status` shows it, and `PresenceWorker` feeds it to `LockDetectionMonitor`, which turns it into
one warning on known to unknown and one info on unknown to known, seeded with the startup reading so the startup
warning is not repeated. While unknown the engine does not pause (`FindPauseReason` pauses only on `== true`): a
fail-closed choice would stop Vigil entirely if a macOS update removed the private key, while a locked screen
merely receives a harmless event; the visible warning is the safeguard. The three key strings are
created and released on each poll (Create rule), which avoids static native state at a negligible cost.
Re-check the private key against new macOS releases; only the owner can test the locked state on a real machine.

## Build, test, publish

```
dotnet build -c Release
dotnet test                                   # runs both test projects (Microsoft Testing Platform)
dotnet publish src/Vigil -c Release -r osx-x64   # Native AOT single binary: src/Vigil/bin/Release/net10.0/osx-x64/publish/vigil
```

Windows code compiles on every OS (the P/Invoke declarations are source-generated and never run elsewhere); the
platform analyzer (CA1416) is satisfied by `[SupportedOSPlatform("windows")]` on the Windows classes and
`OperatingSystem.IsWindows()` guards at the call sites. To see trimming and AOT warnings of the Windows code on a Mac or
Linux machine, publish with `-r win-x64 -p:PublishAot=false -p:PublishTrimmed=true -p:SelfContained=true` (the ILLink
analyzers run; the native compile does not).

The Linux code compiles on every OS too (the P/Invoke declarations are source-generated and never run elsewhere; the Linux-only
classes carry `[SupportedOSPlatform("linux")]` and are reached only behind `OperatingSystem.IsLinux()`). To see the trimming and AOT
warnings of the Linux code on a Mac, publish with `-r linux-x64 -p:PublishAot=false -p:PublishTrimmed=true -p:SelfContained=true`;
the native link itself only happens on Linux (`scripts/package-linux.sh`, CI).

The SDK is pinned in `global.json`. `dotnet test` uses Microsoft Testing Platform because `global.json` sets
`test.runner`; xUnit v3 supports it natively.

Never run `vigil run`, `vigil permissions request`, `vigil install` or `vigil uninstall` on a development machine
just to "see it work": they post input events, show a system prompt, or change your LaunchAgents and launchd state. The tests use a fake platform, so no real input is ever posted. `vigil diag`
is read only and safe.

## Continuous integration

Workflows live in `.github/workflows` (the Linux jobs are described under "Packaging and autostart (Linux)"):

- `ci.yml` runs on pushes to `main` and on pull requests. It builds and tests on Ubuntu, Windows and macOS
  (`dotnet build Vigil.slnx -c Release`, then `dotnet test --solution Vigil.slnx -c Release --no-build`; the Windows
  platform tests are pure logic and run on all three), and runs `scripts/package-macos.sh` for `osx-x64` (Intel
  runner) and `osx-arm64` (Apple silicon runner), uploading each `Vigil.app` as a zip. Native AOT cannot cross-compile
  between operating systems, so each macOS architecture needs its own runner. `package-windows` runs
  `scripts/package-windows.ps1` for `win-x64` and `win-arm64` on `windows-2025` (cross-architecture, see below),
  uploads the zips, and runs `vigil.exe diag` and `vigil.exe permissions request` from the x64 build once as a smoke
  test: the only place the Native AOT Windows binary is started. Both commands are read only and use a temp data directory.
- `release.yml` runs when a tag `v*` is pushed. The tag must equal `v` plus `<Version>` in `Directory.Build.props`.
  It builds all four packages, writes `Vigil-<version>-osx-x64.zip`, `Vigil-<version>-osx-arm64.zip`,
  `Vigil-<version>-win-x64.zip`, `Vigil-<version>-win-arm64.zip` and `SHA256SUMS`, and creates the GitHub Release with
  the preinstalled `gh` CLI. The release notes say the builds are unsigned and that Windows is not verified on hardware.

`package-linux` (`ci.yml`, and in `release.yml`) runs `scripts/package-linux.sh` for `linux-x64` on `ubuntu-24.04` and for
`linux-arm64` on `ubuntu-24.04-arm`, and `ci.yml` starts both binaries once with `diag` and the session variables removed.
The release job adds the two `tar.gz` files to `SHA256SUMS` and to the release.

The macOS job no longer zips inline: `scripts/package-macos.sh` makes `Vigil-<version>-<rid>.zip` with `Vigil.app` and `Vigil Tray.app`
side by side (and verifies both signatures after unpacking it), and both workflows upload that file. Each packaging job also checks,
without starting anything, that the tray is in the package: on macOS the Info.plist keys that must never change
(`CFBundleExecutable` `vigil` and the identifier of `Vigil.app`, the tray's own executable and libraries) and both signatures; on Linux
that `vigil-tray` and the two libraries are in the folder and the tarball, are an ELF program, and which libraries they link to; on Windows
that the zip holds `vigil-tray.exe` and its libraries (the script checks the PE subsystem). A runner has no desktop, so the GUI is never
launched in CI; the logic behind it is covered by unit tests. `AVALONIA_TELEMETRY_OPTOUT` is set in both workflows next to the .NET one
(see rule 1 for what actually guarantees it).

Rules for these workflows: only official `actions/*` actions, each pinned to a full commit SHA with the version in a
comment; `permissions: contents: read` at the top, `contents: write` only on the release job; `checkout` with
`persist-credentials: false`; no secrets (the release job uses the automatic, per-run `github.token`); runner images
pinned by version (`ubuntu-24.04`, `windows-2025`, `macos-15`, `macos-15-intel`) instead of `-latest`;
`DOTNET_CLI_TELEMETRY_OPTOUT` and `DOTNET_NOLOGO` set. To update an action, look up the commit of the new tag
(`gh api repos/actions/<name>/git/ref/tags/<tag>`; if the type is `tag`, resolve it through `git/tags/<sha>`) and
change both the SHA and the comment. The runner labels are listed at
https://docs.github.com/en/actions/reference/runners/github-hosted-runners; check them when an image is retired.

## Tray (`src/Vigil.Tray`, `src/Vigil.App/Tray`)

The tray is a thin client: it talks to the daemon only over the IPC socket (`Status`, `Pause`, `PauseUntilTomorrow`, `Resume`,
`ReloadConfig`) and reads and writes `config.json`. It never posts input, never reads the idle time, requests no OS permission, has no
network code, and never starts, installs or stops the daemon (rule 5). Everything it decides is in `Vigil.App/Tray` and is tested
without Avalonia: `TrayModel` (a `DaemonReply` to icon, state line, today line, tooltip and which actions are possible; the wording is
`StatusFormatter`'s), `TrayCommands` (menu entry to `IpcRequest`), `DaemonClient` (one request, every failure turned into a
`DaemonReply`; the transport is a function so tests need no socket), `SettingsForm`/`SettingsStore`/`SettingsController` (the window's
fields, the config file, save then reload) and `TrayInstanceLock`. `src/Vigil.Tray` only places them: `TrayApplication` (icon, menu,
poll with a `DispatcherTimer`, 5 s, 2 s while the window is open), `SettingsWindow`, `TrayIconRenderer`.

**Settings.** Values stay text in `SettingsForm` until `VigilConfiguration.LoadFromJsonValidated` has read them, so the parser and the rules are
the daemon's and nothing is duplicated. `SettingsStore.Save` edits the file as a JSON tree (`System.Text.Json.Nodes`, which the Native AOT
build can use for objects, arrays and primitives without reflection), because the loader ignores unknown keys and the window shows only
some of the known ones: every key it does not own is written back unchanged, keys are matched without regard to case as the configuration
system does (a duplicate would make the daemon refuse the file), "every day" is written as no `ActiveDays` key, and a file that is not a JSON
object is never overwritten. The write is two passes of the loader: the text as typed (to get the loader's reading and its messages), then
the loader's reading in the documentation's notation, which is validated once more and then written with `InstallFiles.WriteAtomically`
(temporary file in the same directory, mode applied to it, rename; `InstallFiles.PrivateFileMode` is 0600 and is what the log uses too). Comments are
lost (the loader accepts them, the writer cannot keep them). A blank value for a time is "not set", as in the file, so it saves the default.
`SettingsStoreTests` covers the round trip, unknown keys, case, refusal and nothing written, the modes (Unix) and that a concurrent reader
never sees a half-written file.

**Icons.** `TrayIconRenderer` draws the five states with Avalonia's `RenderTargetBitmap` and `DrawingContext` into a 64 px bitmap, so there
are no assets. macOS: black on transparent, marked with `MacOSProperties.SetIsTemplateIcon` (Avalonia's `TrayIcon` passes it to the native
`NSImage.setTemplate`, `trayicon.mm` in Avalonia.Native), so the menu bar tints it. Elsewhere: a coloured disc with a white shape. The
shapes differ per state, so colour is not the only signal. The drawing could not be unit tested (no GUI automation); it was looked at once through
Avalonia.Headless in a scratch project outside the repository, which is not a dependency of the solution.

**Avalonia, what was checked (12.1.4, the latest stable on nuget.org on 2026-10-30; MIT).** Packages added: `Avalonia` (the framework the code is
written against), `Avalonia.Desktop` (`UsePlatformDetect`: the macOS native backend, Win32 and X11, with Skia and HarfBuzz) and
`Avalonia.Themes.Fluent` (the window's controls have no look without a theme). Nothing else is referenced: no Inter font (the system
font is used), no diagnostics, no MVVM or reactive package. Transitive, from the restore of `src/Vigil.Tray` (all 12.1.4 unless named):
Avalonia.Native, Avalonia.Win32 (with Avalonia.Angle.Windows.Natives 2.1.27548.20260419), Avalonia.X11 (with Avalonia.FreeDesktop and
Avalonia.FreeDesktop.AtSpi), Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Remote.Protocol, Avalonia.BuildServices 11.3.2, MicroCom.Runtime 0.11.6,
SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3 with their NativeAssets packages for macOS, Linux, Windows and WebAssembly (the build
copies only the files of the target RID; the WebAssembly assets are never used), and Tmds.DBus.Protocol, which resolves to Vigil's 0.95.2 for
Avalonia.FreeDesktop although it was built against 0.94.1: every one of the 197 member references from Avalonia.FreeDesktop, .AtSpi and
.X11 to that assembly exists with the same signature in 0.95.2 (checked by reading the metadata of both; behaviour is not checked). To
re-check after an upgrade: restore, then compare the member references of those three assemblies with the Tmds assembly in use.
Native AOT: the Avalonia documentation ("Native AOT", docs.avaloniaui.net/docs/deployment/native-aot, last updated May 2026) says to set
`PublishAot` and `IsAotCompatible` and to compile XAML and bindings at build time; this project has no XAML and no bindings. It publishes for
`osx-x64` with no warning (`TreatWarningsAsErrors` is on), and trimming analysis for linux-x64, linux-arm64, win-x64 and win-arm64
(`-p:PublishAot=false -p:PublishTrimmed=true -p:SelfContained=true`) gives none either; the native compile of those four has not been run.
`InvariantGlobalization` is on, like the other programs: Avalonia's documented Linux requirements list no ICU.

*No network, no telemetry.* Run time: no Avalonia, Skia or HarfBuzz assembly of that version references `System.Net.Http`; the only ones
that use `System.Net.Sockets` are Avalonia.FreeDesktop.AtSpi (Unix domain socket), Avalonia.Remote.Protocol and Avalonia.DesignerSupport (TCP, the
design-time previewer; the macOS Native AOT tray contains none of their TCP or DNS types: no `TcpListener`, `TcpClient`, `Dns`), and a search of
the library source (tag 12.1.4) and of its macOS native code for network types, URLs, update and telemetry names found nothing at run time.
Build time: `Avalonia.BuildServices` is a transitive dependency of `Avalonia`; its target `AvaloniaStats` runs before `CoreCompile` and sends
anonymous build statistics (the package's README lists them: hashed project name, output type, target framework, runtime identifier, Avalonia
version, a machine identifier, operating system, development environment, CI detection) unless `AVALONIA_TELEMETRY_OPTOUT=1` is in the environment,
and only while the property `UsedAvaloniaProducts` is not empty (the target's condition). `Directory.Build.targets` clears that property after
Avalonia's props set it and has a guard target that fails the build if it is not empty, the same way the xunit telemetry hook is handled. After
an Avalonia upgrade, check that `msbuild -pp` still shows the target conditioned on that property, and build once with
`-p:UsedAvaloniaProducts=AvaloniaUI` to see the guard fail. (On the machine where this was developed the task ran once, before the guard
existed, in a first build of an empty Avalonia project.)

**Linux.** `UsePlatformDetect` on Linux is `UseX11` only (`AppBuilderDesktopExtensions`): the Wayland backend is a separate opt-in package
that is not referenced, so on Wayland the tray needs XWayland. The tray icon is StatusNotifierItem over D-Bus (`DBusTrayIconImpl`), with the
XEmbed tray as the fallback when there is no session bus connection. Required libraries per Avalonia's documentation: `libx11-6 libice6 libsm6
libfontconfig1`; the X11 backend also loads libXi, libXrandr, libXcursor, libXext and libXfixes by name (`Avalonia.X11`), and `libSkiaSharp.so`
links `libfontconfig.so.1` and glibc 2.27 (read from the file with `objdump`). `libgtk-3` is used by Avalonia only for native dialogs, which the
tray does not open.

## Windows notes

Every Win32 declaration in `Vigil.Platform.Windows/NativeMethods.cs` was checked against its Microsoft Learn page, which
the comment above it names; do the same for a new one, and do not write a signature from memory.

- **Idle time**: `GetLastInputInfo` returns a 32-bit tick count in the units of `GetTickCount`, which wraps every
  49.7 days, so `IdleTimeCalculator` subtracts modulo 2^32 (covered by `IdleTimeCalculatorTests`). The documentation
  says the last-input tick is not guaranteed to be incremental (an event from `SendInput` supplies its own), so a
  difference of 2^31 ms or more is read as "slightly in the future" and gives zero. `GetTickCount64` is not used: it
  would need truncating to compare with a DWORD, and the documentation does not define that.
- **Injection**: `SendInput`, one event per call, return value 1 or a failure with `GetLastError` in the message. A
  return of 0 with error 0 is what UIPI blocking looks like, according to the documentation, and the message says so.
  `MouseNudge` is a relative move of zero pixels: relative moves are scaled by pointer speed and acceleration, with
  unspecified rounding, so a +1/-1 pair is not documented to cancel; an absolute move back to the `GetCursorPos`
  position needs a pixel to normalized-coordinate conversion the documentation does not define, and `GetCursorPos`
  needs the input desktop. Zero times any scale is zero. Whether Windows counts such an event as input is what the
  idle verification checks at run time; `F15KeyUp` is the fallback. Neither has been verified on a real machine.
- **Lock state**: `SessionFlags` of `WTSINFOEX_LEVEL1_W`, read from the first 20 bytes of the `WTSINFOEXW` buffer
  (`WtsInfoExHead`: Level at 0, SessionId 8, SessionState 12, SessionFlags 16). The offsets follow from the documented
  field order and types and the 8-byte alignment of `LARGE_INTEGER`; `WindowsNativeLayoutTests` re-derives them from a
  full managed copy of the structure. The constants `WTS_CURRENT_SESSION` and `WTS_CURRENT_SERVER_HANDLE` are not given
  numerically on Learn; the values are those of the MinGW-w64 and Wine headers. Only the locked/unlocked flags are
  used; `SessionState` (disconnected, switched away) is read but not acted on, so a switched-away session is not
  paused.
- **Data directory and ACLs**: no mode bits on Windows, and Vigil does not set an ACL; the directory inherits its
  parent's rules (`PrivateDirectory` documents this, the Unix-mode branches in `IpcServer`, `RotatingFileWriter` and
  `InstallFiles` point to it). The default `%LOCALAPPDATA%\Vigil` is inside the user profile.
- **Sockets**: `AF_UNIX` works on Windows 10 1803 and later (announced in Windows build 17063,
  https://devblogs.microsoft.com/commandline/af_unix-comes-to-windows/); `sun_path` is 108 bytes, so the existing
  100-byte limit holds on all three platforms. A stale socket file after a crash is removed by the lock holder, as on Unix.
- **Log rotation**: see `RotatingFileWriter`. A rename needs delete access, which depends on the other handles'
  `FileShare.Delete`, not on the writer's, so the fix is a copy-and-truncate fallback and not a share flag on the
  writer. Pinned by `RotatingFileLogTests.Rotation_works_while_a_reader_holds_the_file_open_without_sharing_delete`
  (the regression test on Windows, a plain rename on Unix). Known limits: if `vigil.log.1` itself is held without
  sharing, rotation fails and lines are dropped until it is released (the writer never throws).
- **vigild.exe**: the process has no console, so `Console.Out` and `Console.Error` are the null stream
  (`ConsolePal.Windows.cs`: `GetStandardFile` returns `Stream.Null` for an invalid handle); everything that ends a
  run is already written to `vigil.log` for the LaunchAgent, and `VigilApplication.RunDaemonAsync` does the same for a
  bad command line. It is stopped by `IpcCommand.Shutdown`.

## Packaging and autostart (Windows)

```
scripts/package-windows.ps1 win-x64      # or win-arm64; output: artifacts/<rid>/{vigil.exe,vigild.exe,tray/,Vigil-<version>-<rid>.zip}
```

The script publishes `src/Vigil`, `src/Vigild` and `src/Vigil.Tray` with Native AOT, checks that `vigil.exe` has the console subsystem and
`vigild.exe` and `vigil-tray.exe` the Windows (no console) subsystem by reading the PE header, and zips them, plus the repo's `LICENSE`
(the MIT license requires its notice in distributed copies), under a folder named `Vigil`. The tray is not a single file: Skia, HarfBuzz and
ANGLE come as `.dll` files (`libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`) that it loads from its own folder, so it goes into
`Vigil/tray/` with them (debug symbols left out), not next to `vigild.exe`, whose folder `vigil install` points the Run key at. The script
has not been run on Windows by the person who wrote it (no PowerShell was available): it was reviewed, and CI is its first run.
Nothing is signed. Native AOT can cross-compile between x64 and arm64 on Windows when the target's C++ build tools are
installed (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile); the `windows-2025` image
lists `Microsoft.VisualStudio.Component.VC.Tools.ARM64` and `.x86.x64`, so CI builds both `win-x64` and `win-arm64` there.
GitHub also offers a native Arm label for public repositories, `windows-11-arm`
(https://docs.github.com/en/actions/reference/runners/github-hosted-runners); it is not used, because the cross-build is
documented and one runner type is simpler. If the cross-build ever fails, that label is the alternative.

`vigil install` / `vigil uninstall` on Windows are implemented by `Install/RunKeyManager` on top of `IRunKey`
(`Vigil.Platform.Windows/Autostart`; `RunKey` is the HKEY_CURRENT_USER Run key through `Microsoft.Win32.Registry`, which
is part of the shared framework, uses source-generated interop and needs no admin rights; tests use a fake and never
touch the registry) and `IDaemonStopper` (`IpcDaemonStopper` sends `IpcCommand.Shutdown` and waits until the socket stops
answering; tests use a fake or a real server with a fake handler). The rules mirror `LaunchAgentManager`:

- The value is `Vigil`, REG_SZ, `"<folder>\vigild.exe"` (quoted), plus `--data-dir "<dir>"` when `VIGIL_DATA_DIR` names a
  directory other than the default (`DaemonArguments` builds and parses it). A Run key command line may be 260
  characters at most (https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys); longer is refused.
- An existing value is compared as text with what would be written. Identical: nothing changes. Different or not plain
  text: refused without `--force`. Install also starts the daemon, as the macOS flow does with `launchctl bootstrap`:
  `IDaemonProbe` (`IpcDaemonStopper.IsRunning`, the same socket probe `Stop` waits on) says whether a daemon already
  serves the data directory, and `IDaemonLauncher` (`ProcessDaemonLauncher`: `Process.Start`, `UseShellExecute = false`,
  `CreateNoWindow = true`, not waited for; tests use a fake, so no process is ever started) starts `vigild.exe` with
  `DaemonArguments.BuildArguments`, the very string `Build` puts in the value. Identical value and daemon running:
  nothing. Identical value, daemon not running: only the start (macOS: unchanged plist not loaded, only bootstraps).
  A start failure exits 1 and leaves the value written (macOS: a failed bootstrap leaves the plist). Replacing a differing value
  (`--force`) while a daemon is running stops it first with `IDaemonStopper` (as `uninstall` does), and only then writes
  the new value and starts the daemon; if the stop fails, the old value stays, nothing starts and the exit code is 1
  (macOS: bootout before the plist changes). A fresh install with a daemon already running (for example `vigil run`)
  starts nothing and leaves that daemon alone.
- `uninstall` stops the daemon first and keeps the value if that fails, then deletes the value; `--purge` goes through
  `InstallFiles.Purge`, shared with the macOS flow: only the default directory named `Vigil`, never a link or reparse
  point, and on Windows retried a few times because the daemon may still be closing its files.
- `IpcCommand.Shutdown` exists only for this. The handler answers first and stops the host in `IIpcHandler.ResponseSent`,
  after the response was written, so the client never sees a cut connection. It is as protected as the other IPC
  commands (the socket lives in the user's data directory); there is deliberately no `vigil stop` command.

## Packaging and autostart (macOS)

```
scripts/package-macos.sh osx-x64      # or osx-arm64; output: artifacts/<rid>/{Vigil.app,"Vigil Tray.app",Vigil-<version>-<rid>.zip}
```

**Why the tray is a second bundle.** The Accessibility permission belongs to the bundle that posts the events, and the documented check
(`open -n -o ... ~/Applications/Vigil.app --args diag`) runs the bundle's `CFBundleExecutable`, which must stay `vigil`. Putting `vigil-tray`
inside `Vigil.app` would leave it without a launchable identity (opening the app starts `vigil`), would put about 37 MB (the tray program, 17 MB, and Avalonia's native libraries, 20 MB; osx-x64)
into the bundle whose signature changes the permission, and would give the tray, which needs no permission, the identity that has one.
`Vigil Tray.app` has its own identifier (`io.github.yasarbudak.vigil.tray`), `LSUIElement` (no Dock icon), and holds `vigil-tray` and the three
Avalonia native libraries (`libAvaloniaNative`, `libSkiaSharp`, `libHarfBuzzSharp`, `.dylib`) next to it, where the Native AOT program finds
them (it was started from this layout). Each library is signed ad hoc first, then the bundle; `codesign --verify --strict --deep` passes, and
the script repeats it on the bundles unpacked from the zip. `Vigil.app` is built exactly as before.

The script runs the Native AOT publish, assembles `Vigil.app` (`Contents/MacOS/vigil`, an `Info.plist` with
`CFBundleIdentifier` `io.github.yasarbudak.vigil`, the version from `<Version>` in `Directory.Build.props`,
`LSMinimumSystemVersion` 12.0, `LSUIElement` true, and `Contents/Resources/LICENSE`, copied before signing so the
signature seals it) and signs it ad hoc with that identifier. Every build produces a
new ad-hoc signature, so the Accessibility entry must be enabled again after each rebuild. `artifacts/` is ignored by git.

`vigil install` / `vigil uninstall` are implemented by `Install/LaunchAgentManager` (orchestration, file handling,
purge safety) on top of three pieces in `Vigil.Platform.MacOS/LaunchAgent`: `AppBundle` (lexical check that the
executable is `X.app/Contents/MacOS/<name>`), `LaunchAgentPlist` (pure, write-only rendering) and `ILaunchctl`
(`Launchctl` runs `/bin/launchctl` against `gui/<uid>`; tests use a fake and never call it). Design points,
verified in `man launchd.plist` and `man launchctl`:

- `KeepAlive` is `{ Crashed = true }`, not `true` and not `SuccessfulExit = false`. `SuccessfulExit = false`
  restarts on any non-zero exit, which includes the daemon's deliberate exits (2 invalid config, 1 platform or
  second instance). `Crashed` restarts only after a crash signal. `RunAtLoad` is explicit.
  `ThrottleInterval` is 30 s (default 10 s) to slow a deterministic crash loop.
- `AssociatedBundleIdentifiers` names the app in System Settings, Login Items.
- Modern subcommands: `bootstrap gui/<uid> <plist>`, `bootout gui/<uid>/<label>`, `print gui/<uid>/<label>`
  (exit status only; its output is documented as not being API).
- An existing plist is never parsed: it is compared byte for byte with what `Render` produces for the current
  executable and environment. Identical means idempotent; anything else (including unreadable or binary content)
  needs `--force`. This keeps System.Xml out of the Native AOT binary (parsing cost about 3.4 MB, measured).
- The plist is written to a temporary file next to the target, set to 0644 and renamed over it. The plist has no
  stdout/stderr paths (see Logging). `install` pre-creates `vigil.log` 0600 (and tightens an existing one).
- `uninstall --purge` deletes only the default data directory (`VigilPaths.DefaultDirectory()`, named `Vigil`, not a
  symbolic link); a `VIGIL_DATA_DIR` override is never purged.
- The label is also the bundle identifier; keep `LaunchAgentManager.Label` and the script's `BUNDLE_ID` equal.

## Linux notes

Everything below was checked against the source it names; do the same for a change, and do not write a signature, a D-Bus
name or a constant from memory. **None of it has run on a real Linux desktop yet**: the logic is covered by unit tests, the
native declarations by layout tests, and the binary starts headless in CI.

- **Session type**: `SessionTypeDetector` decides in this order: `XDG_SESSION_TYPE` (`x11` or `wayland`; pam_systemd sets it),
  then `WAYLAND_DISPLAY`, then `DISPLAY`. `XDG_SESSION_TYPE` and `WAYLAND_DISPLAY` come before `DISPLAY` because a Wayland session
  also has `DISPLAY` (XWayland). `Linux.Backend` chooses only how input is posted; the idle source and the lock state follow
  the session type. With no session in the environment `LinuxPlatformServices.StartupProblem` is set and the daemon refuses
  to start (`PresenceWorker.StartAsync`, exit 1, which the unit does not restart); `vigil diag` still prints everything it can.
- **X11** (`X11/`): the three libraries are probed with `NativeLibrary.TryLoad` before the first call
  (`X11Libraries.CheckAvailable`), so a missing library is a message, not a crash. `XScreenSaverInfo` is allocated by
  libXss (`XScreenSaverAllocInfo`) and read at the LP64 offsets pinned by `LinuxNativeLayoutTests`. Posting is
  `XTestFakeRelativeMotionEvent` +1 and -1 (zero net movement) or the key-up of F15 (`XKeysymToKeycode` of keysym 0xffcc),
  then `XSync` (which includes the flush) so that protocol errors reach the process's error handler before the call returns:
  without an error handler Xlib exits the process. A lost X connection still ends the process (the Xlib I/O error handler may not
  return); under the unit that is the end of the session. The X server does not drop a key release for a key that is not
  down (`GetKeyboardEvents` in `dix/getevents.c` only drops a repeated press); whether the server's idle timer counts an XTest
  event is what the run-time verification checks.
- **Wayland idle**: only GNOME, `org.gnome.Mutter.IdleMonitor.GetIdletime` (uint64 milliseconds, from mutter's
  `meta_idle_monitor_get_idletime`). KDE's `org.freedesktop.ScreenSaver.GetSessionIdleTime` exists, is documented in its XML
  as `seconds` but returns the KIdleTime value, and answers `NotSupported` on Wayland (kscreenlocker `interface.cpp`), so it is not
  used. A compositor without the monitor gives `LinuxUnavailableException` with that explanation.
- **D-Bus** (`DBus/`): `DBusValue`, `DBusCall`, `DBusReply` and `DBusSignal` are Vigil's own small model, and
  `IDBusConnection` is the seam; `TmdsDBusConnection` is the only file that uses Tmds.DBus.Protocol (0.95.2; the project has no
  1.0 release, so expect API changes: the generic `Reader.ReadVariantValue` path and `MessageWriter` are used, not the methods
  marked `Obsolete`/`RequiresUnreferencedCode`). Tests use `FakeDBusConnection` and `FakePortalConnection`; no test touches a
  real bus. Only `unix:` addresses are accepted (`DBusAddresses`), so no D-Bus setting can make Vigil open a network
  connection.
- **Portal** (`Portal/`): `PortalProtocol` builds the calls (CreateSession, SelectDevices with `types` 3, `persist_mode` 2 and
  the `restore_token`, Start with an empty `parent_window`, NotifyPointerMotion, NotifyKeyboardKeysym) and the Request path
  (`.../request/SENDER/TOKEN`); `PortalClient` runs the three requests, subscribing to each `Response` signal before the call;
  `PortalSessionManager` holds the policy: the daemon (`Silent`) never lets a dialog stay (15 s, then `Request.Close` and the
  token is deleted), only `RequestConsentAsync` (`Interactive`) waits for the user, and the token the portal returns replaces the
  one that was used. `RequestConsentAsync` runs in the daemon, only for `IpcCommand.RequestPermission` (`IpcHandler.HandleAsync`, from
  `vigil permissions request`; `PlatformServices.DaemonConsent`, set only for the portal backend): one request at a time (a second gets
  `ConsentOutcome.InProgress`), the new session replaces the open one and is the one injected through, and while a request waits
  `Inject` does not start a session from the saved token (it would use the token up). The CLI never asks the portal itself on this
  backend (`PortalInputBackend.RequestAccess` only reports whether a consent exists); without a daemon it prints how to start one and
  exits 1. macOS keeps asking in the CLI process (TCC attributes the prompt to the bundle); Windows, X11 and uinput have no
  `DaemonConsent`, and a daemon without one rejects the command. The portal answers Response 1 for a refusal and for a cancelled dialog
  alike, so `Cancelled` covers both; `Denied` is a consent without the device the strategy needs. If the CLI is interrupted the daemon cancels
  the request (see the IPC paragraph above). The RemoteDesktop interface also offers `ConnectToEIS` (libei), which its
  description recommends over the D-Bus `Notify*` methods; Vigil uses the D-Bus methods because they need no fd passing and no
  extra library, and the interface keeps them for this use. Why the daemon asks: the portal derives a host
  application's identity from the process's systemd unit (`shared/xdp-app-info-host.c`) and scopes a restore token to it
  (`xdp-session-persistence.c`, `get_persistent_permissions` looks the token up under `session->app_id`), so a consent given
  from a terminal may not restore in `vigil.service`; the request therefore runs in the daemon. Still unverified on a real desktop:
  that the daemon's consent restores after a restart (registering an application id was not built).
- **uinput** (`Uinput/`): ioctl numbers are computed by `IoctlNumber` with the generic layout (x86 and arm64) and pinned to the
  known values by `LinuxNativeLayoutTests`. The input core drops an `EV_REL` event with value 0 and a key release for a key that
  is not down (`input_get_disposition` in `drivers/input/input.c`), so the nudge is +1/-1 and the F15 event is press and release.
  The device is created at the first injection and kept; the first use waits one second, as `Documentation/input/uinput.rst`
  does after `UI_DEV_CREATE`.
- **Lock and battery**: `LogindSessionLock` reads `LockedHint` of `/org/freedesktop/login1/session/auto` (logind resolves `auto`
  to the caller's session, or the user's display session, which a systemd user service needs); `PowerSupplyClassifier` is pure
  and covered by `LinuxPowerSupplyTests`.
- **Data directory**: `$XDG_DATA_HOME/vigil`, `~/.local/share/vigil` by default (`VigilPaths.LinuxDefaultDirectory`). The socket
  path limit (sun_path 108, Vigil refuses more than 100) is unchanged. `InstallFiles.Purge` accepts the lower-case default name.
- **Logging**: `JournalDetection` compares `JOURNAL_STREAM` with the socket inode of `/proc/self/fd/{2,1}`; under the journal
  `DaemonHost` uses the console logger's systemd format and no `vigil.log`. `PresenceWorker` logs a failure that repeats at every
  poll once (`RepeatedFailureTracker`).

## Packaging and autostart (Linux)

```
scripts/package-linux.sh linux-x64      # or linux-arm64; output: artifacts/<rid>/{vigil,tray/,Vigil-<version>-<rid>.tar.gz}
```

The script publishes `src/Vigil` and `src/Vigil.Tray` with Native AOT (needs `clang` and `zlib1g-dev`), prints the glibc version each binary
asks for (`objdump -T`), and packs `vigil`, `tray/` (`vigil-tray`, `libSkiaSharp.so`, `libHarfBuzzSharp.so`, which the tray loads from its
own folder) and `LICENSE` under a folder named `Vigil` with owner 0 and sorted names. Native AOT cannot
cross-compile between operating systems, and cross-architecture on Linux needs a cross linker and the target's C runtime and
zlib objects, which the .NET documentation says are steps that "may suffice ... although this is not documented or guaranteed"
(https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile). GitHub has a native Arm runner for public
repositories, `ubuntu-24.04-arm`
(https://docs.github.com/en/actions/reference/runners/github-hosted-runners), so each architecture is built on its own runner and
the script refuses to build for the other one. `-latest` labels are not used, as for the other jobs. The script has been
syntax-checked and its guards run, but its build and packing steps have only been exercised in CI (never on the maintainer's
machine); the tray part has not been run at all yet (the publish of the tray for the four non-macOS runtime identifiers was only trim-analysed).

`vigil install` / `vigil uninstall` on Linux are implemented by `Install/SystemdUnitManager` on top of `SystemdUserUnit` (pure,
write-only rendering) and `ISystemctl` (`Systemctl` runs `systemctl --user`; tests use a fake and never call it). The rules mirror
`LaunchAgentManager`:

- The unit is `~/.config/systemd/user/vigil.service` (`$XDG_CONFIG_HOME/systemd/user`). `Restart=on-abnormal` and `RestartSec=30`
  are the macOS `KeepAlive { Crashed }` and `ThrottleInterval`: the table in systemd.service(5) restarts `on-failure` after any
  non-zero exit, which includes the deliberate exits 1 and 2 (it would need `RestartPreventExitStatus=1 2`), and `on-abnormal`
  only after a signal, a timeout or the watchdog. `WantedBy=`, `PartOf=` and `After=graphical-session.target` tie it to the
  session (systemd.special(7)). Quoting follows systemd.syntax(7): `\`, `"` escaped, `%` written `%%`, and `$` written `$$` in the command
  line (not in `Environment=`).
- An existing unit is never parsed: it is compared byte for byte with what `Render` produces. Identical means idempotent;
  anything else needs `--force`, which runs `disable --now` first. The unit is written atomically (`InstallFiles.WriteAtomically`,
  shared with the macOS plist) with mode 0644.
- Install checks that `systemctl --user show-environment` works (its output, the manager's environment, is never kept or shown) and
  refuses with a message otherwise. It does not prepare `vigil.log`: the journal keeps the log.
- `uninstall --purge` deletes only the default data directory, which is named `vigil` here (`InstallFiles.Purge` accepts `Vigil` and
  `vigil`).

## Adding a platform

1. Create `src/Vigil.Platform.<Name>` referencing Vigil.Core, marked AOT compatible and platform-attributed. Keep the
   constants and structures that pure code uses outside the platform-attributed class (see `Win32Constants`), so the
   pure code and its tests do not need the attribute.
2. Implement `IIdleSource`, `IInputInjector`, `ISessionState`, `IPermissionProbe` (and `IPermissionRequester` if
   the OS has a permission to request). Return `null` from `ISessionState` members you cannot determine.
3. Verify every native signature and constant against the OS headers or documentation and cite it in a comment.
4. Add a branch in `PlatformServices.Create` and a project reference from `src/Vigil.App`. Do not touch Core.
5. Add platform options (if any) as a section in `config.json`, like `MacOS` and `Windows` (`PlatformOptions` carries
   them to the factory).

## Rules contributors must keep (trust guarantees)

These mirror the "Privacy & trust" section of the README. A change that breaks one needs an explicit decision
by the owner first.

1. No network. Do not use or reference `System.Net.Http`, and add no sockets other than the local Unix domain
   socket (D-Bus on Linux is also a local Unix socket: `DBusAddresses` accepts `unix:` addresses only, because Tmds.DBus.Protocol
   could also connect over TCP). No update checks, no telemetry. `Directory.Build.targets` removes the `System.Net.Http` implicit using;
   `grep -rn "System.Net" src/` must show only `System.Net.Sockets` for the IPC socket.
   Test projects too: xunit.v3 pulls in `Microsoft.Testing.Extensions.Telemetry`, which would send usage data
   from the Microsoft Testing Platform test host. Microsoft documents only environment variables as the opt-out
   (`TESTINGPLATFORM_TELEMETRY_OPTOUT=1` or `DOTNET_CLI_TELEMETRY_OPTOUT=1`,
   https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-telemetry), which depend on each
   developer's and CI's environment. So `Directory.Build.targets` removes the extension's
   `TestingPlatformBuilderHook` item, the documented self-registration mechanism, so it is never compiled into
   the test host's `SelfRegisteredExtensions`, and a guard target fails the build if a telemetry hook is
   registered again (for example after an xunit upgrade changes the hook id). Check after upgrading xunit:
   `dotnet test --project tests/Vigil.Core.Tests --diagnostic --diagnostic-output-directory <dir> --diagnostic-verbosity Trace`
   and confirm the log says `Telemetry collector provider: '...NopTelemetryService'` and contains no
   `Send telemetry event` line. The DLL is still restored and copied to the test output but is never loaded.
   The tray's UI library has one build-time data collector of the same kind: `Avalonia.BuildServices` (see "Tray"). `Directory.Build.targets`
   empties `UsedAvaloniaProducts`, which that target is conditioned on, and fails the build if it is not empty. A tray built by this
   repository sends nothing at build time or run time, and uses no network: Avalonia, Skia and HarfBuzz reference no HTTP type, and the macOS
   Native AOT binary holds no TCP or DNS type.
2. Read nothing but the OS idle counter. No event taps, no keystroke, mouse, window-title or screen reading. On
   macOS only post-event access may be requested, never Input Monitoring or any listen-event API. On Windows no
   `SetWindowsHookEx`, no raw input registration, no `GetAsyncKeyState`; the only reads are `GetLastInputInfo`,
   `GetTickCount`, the session flags and the power status. On Linux no `XQueryPointer`, no grabs, no `XSelectInput`, no XRecord or
   XInput, no `/dev/input/event*`, no portal ScreenCast or clipboard; the only reads are the X server's idle counter, the GNOME
   idle monitor, the logind locked hint, four sysfs power-supply attributes and the process's own `/proc/self/fd` links. The portal
   session asks for keyboard and pointer only to post input.
3. Least privilege. Never require root or administrator rights (on Windows everything is per user: HKEY_CURRENT_USER and
   the user's profile; on Linux the unit is a user unit, and Vigil never installs the udev rule or the group that the optional
   `uinput` backend needs: the user does that, with their own `sudo`, and selects the backend in `config.json`). A prompt may only
   be started by `vigil permissions request` (macOS Accessibility in the CLI process; the Linux portal consent in the daemon, run only in
   answer to `IpcCommand.RequestPermission`); `IPermissionProbe` never prompts, and
   the daemon's portal start is bounded and closes the request instead of leaving a dialog (`PortalSessionManager`).
4. Data stays local. The activity log stores counters and timestamps only, never content, only in the per-user
   data directory (0700; files 0600), and must stay switchable via `ActivityLog.Enabled`. The IPC socket is
   owner-only. Daily files are deleted after `ActivityLog.RetentionDays` (default 30, validated 1 to 3650) by
   `JsonlActivityLog.Prune`, run by `ActivityLogRetention` at daemon start and at day rollover. Prune deletes
   only regular files whose name `VigilPaths.TryParseActivityLogDay` accepts (`activity-yyyy-MM-dd.jsonl`) and
   whose day is older than the cutoff; it never recurses, never follows or removes symbolic links, and never
   touches any other file.
5. No silent persistence. Never write LaunchAgents, Run keys, login items, services or any autostart on the user's
   behalf without an explicit user command. The only such command is `vigil install` (macOS, Windows, Linux), removed by
   `vigil uninstall`; `AutostartCommand` is the only caller of `LaunchAgentManager`, `RunKeyManager` and `SystemdUnitManager`, and
   `RunKey` is the only code that touches the registry. No daemon, status, diag or update path may call them, and neither may the tray:
   it adds no login item, Run value, LaunchAgent or unit, and never starts the daemon; its autostart is something the user sets up by hand. Tests never
   write the real Run key, never call the real `launchctl` or `systemctl`, and never touch `~/.config`.
6. Add no NuGet package without the owner's approval; versions are pinned centrally in `Directory.Packages.props`. Approved for the tray,
   referenced only by `Vigil.Tray`: Avalonia, Avalonia.Desktop and Avalonia.Themes.Fluent 12.1.4 (MIT; see "Tray" for the check of what
   they bring). Their dependencies are not referenced directly. The one
   approved for Linux is Tmds.DBus.Protocol 0.95.2 (MIT; the project documents it as NativeAOT and trimming compatible,
   https://github.com/tmds/Tmds.DBus/blob/main/docs/protocol.md); only `Vigil.Platform.Linux` references it, and it adds nothing
   to the macOS binary (the Linux branch is removed at compile time, the sizes were identical with and without it).
7. No secrets in code, config or docs.
8. Ship the licenses of what ships. `THIRD-PARTY-NOTICES.txt` at the repository root holds, unchanged and with the source of each text
   named, the licenses of the third-party code in the binaries (the .NET runtime, Tmds.DBus.Protocol, Avalonia, MicroCom.Runtime, SkiaSharp
   and HarfBuzzSharp with the native libraries' notices, ANGLE). The packaging scripts refuse to build without it and put it next to `LICENSE` in every
   package, CI checks it is there, and the macOS script compares the copies inside the unpacked zip with the file. Add a package or upgrade
   one: copy the new texts from the package or the tagged upstream source, never from memory, and update the section.
