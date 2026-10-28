# Developer guide

## Working principle: no vibecoding

AI tools may be used as a pair partner. The developer leads, the tool produces, the developer verifies.
Every line must be understood, explainable and defensible by the person who commits it.

## Architecture

```
src/Vigil.Core            platform-free logic and abstractions (AOT compatible, no I/O)
src/Vigil.Platform.MacOS  macOS implementations (LibraryImport P/Invoke to CoreGraphics, IOKit, CoreFoundation),
                          and the pure/OS-facing LaunchAgent pieces (plist, bundle check, launchctl)
src/Vigil                 the `vigil` executable: CLI, daemon host, IPC, config, activity log, platform composition
tests/Vigil.Core.Tests    engine, schedule, options, duration parser (hand-written fakes, no mocking library)
tests/Vigil.Tests         app layer: IPC server/client, activity log, config binding, daemon host with a fake platform
```

Layer responsibilities:

- **Core** decides. `PresenceEngine` is a deterministic state machine fed with observations and injection
  results; all time comes from `TimeProvider`. `PresenceCycle` is one poll step (observe, decide, inject, wait,
  re-read idle, report). Core talks to the world only through `IIdleSource`, `IInputInjector`, `ISessionState`,
  `IPermissionProbe`, `IPermissionRequester` and `IActivityLog`.
- **Platform.\*** implements those interfaces for one OS and nothing more. Each P/Invoke carries a comment naming
  the SDK header it was verified against. CoreFoundation objects are owned by `CFTypeHandle` (SafeHandle).
- **Vigil** wires everything: `PlatformServices` (the only OS switch), `DaemonHost` (composition root),
  `PresenceWorker` (poll loop on `PeriodicTimer`), `IpcServer`/`IpcClient` (newline-delimited JSON over a Unix
  domain socket), `VigilPaths` (the only place that knows file locations), `JsonlActivityLog` (with
  `ActivityLogRetention` for the retention cleanup), `LaunchAgentManager` (install/uninstall), commands.

Single instance and IPC: `IpcServer` holds an exclusive lock on `vigil.sock.lock` for the daemon's lifetime. Only
the lock holder may delete or recreate the socket, and a second daemon fails at the lock. The IPC server is
registered before `PresenceWorker`, so a second daemon fails before anything can inject or write the activity log.
The server also makes its directory 0700 itself before binding, serves clients concurrently (limit 8, 5 s
timeout each), and never lets an accept or handler error end the loop.

Configuration: `VigilConfiguration` is the single place that reads and binds `config.json` (`Load`, `LoadValidated`).
The daemon validates before building the host; `diag` and `permissions request` use the same loader. A bad file
raises `ConfigurationException`, which the commands print without a stack trace (exit code 2).

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

The SDK is pinned in `global.json`. `dotnet test` uses Microsoft Testing Platform because `global.json` sets
`test.runner`; xUnit v3 supports it natively.

Never run `vigil run`, `vigil permissions request`, `vigil install` or `vigil uninstall` on a development machine
just to "see it work": they post input events, show a system prompt, or change your LaunchAgents and launchd state. The tests use a fake platform, so no real input is ever posted. `vigil diag`
is read only and safe.

## Packaging and autostart (macOS)

```
scripts/package-macos.sh osx-x64      # or osx-arm64; output: artifacts/<rid>/Vigil.app
```

The script runs the Native AOT publish, assembles `Vigil.app` (`Contents/MacOS/vigil`, an `Info.plist` with
`CFBundleIdentifier` `io.github.yasarbudak.vigil`, the version from `<Version>` in `src/Vigil/Vigil.csproj`,
`LSMinimumSystemVersion` 12.0, `LSUIElement` true) and signs it ad hoc with that identifier. Every build produces a
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
- The plist is written to a temporary file next to the target, set to 0644 and renamed over it. The log file is
  pre-created 0600 so launchd opens an existing private file.
- `uninstall --purge` deletes only the default data directory (`VigilPaths.DefaultDirectory()`, named `Vigil`, not a
  symbolic link); a `VIGIL_DATA_DIR` override is never purged.
- The label is also the bundle identifier; keep `LaunchAgentManager.Label` and the script's `BUNDLE_ID` equal.

## Adding a platform

1. Create `src/Vigil.Platform.<Name>` referencing Vigil.Core, marked AOT compatible and platform-attributed.
2. Implement `IIdleSource`, `IInputInjector`, `ISessionState`, `IPermissionProbe` (and `IPermissionRequester` if
   the OS has a permission to request). Return `null` from `ISessionState` members you cannot determine.
3. Verify every native signature and constant against the OS headers or documentation and cite it in a comment.
4. Add a branch in `PlatformServices.Create` and a project reference from `src/Vigil`. Do not touch Core.
5. Add platform options (if any) as a section in `config.json`, like `MacOS`.

## Rules contributors must keep (trust guarantees)

These mirror the "Privacy & trust" section of the README. A change that breaks one needs an explicit decision
by the owner first.

1. No network. Do not use or reference `System.Net.Http`, and add no sockets other than the local Unix domain
   socket. No update checks, no telemetry. `Directory.Build.targets` removes the `System.Net.Http` implicit using;
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
2. Read nothing but the OS idle counter. No event taps, no keystroke, mouse, window-title or screen reading. On
   macOS only post-event access may be requested, never Input Monitoring or any listen-event API.
3. Least privilege. Never require root or administrator rights.
4. Data stays local. The activity log stores counters and timestamps only, never content, only in the per-user
   data directory (0700; files 0600), and must stay switchable via `ActivityLog.Enabled`. The IPC socket is
   owner-only. Daily files are deleted after `ActivityLog.RetentionDays` (default 30, validated 1 to 3650) by
   `JsonlActivityLog.Prune`, run by `ActivityLogRetention` at daemon start and at day rollover. Prune deletes
   only regular files whose name `VigilPaths.TryParseActivityLogDay` accepts (`activity-yyyy-MM-dd.jsonl`) and
   whose day is older than the cutoff; it never recurses, never follows or removes symbolic links, and never
   touches any other file.
5. No silent persistence. Never write LaunchAgents, login items, services or any autostart on the user's behalf
   without an explicit user command. The only such command is `vigil install` (macOS), removed by `vigil uninstall`;
   `AutostartCommand` is the only caller of `LaunchAgentManager`. No daemon, status, diag or update path may call it.
6. Add no NuGet package without the owner's approval; versions are pinned centrally in `Directory.Packages.props`.
7. No secrets in code, config or docs.
