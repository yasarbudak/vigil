# Developer guide

## Working principle: no vibecoding

AI tools may be used as a pair partner. The developer leads, the tool produces, the developer verifies.
Every line must be understood, explainable and defensible by the person who commits it.

## Architecture

```
src/Vigil.Core            platform-free logic and abstractions (AOT compatible, no I/O)
src/Vigil.Platform.MacOS  macOS implementations (LibraryImport P/Invoke to CoreGraphics, IOKit, CoreFoundation)
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
  domain socket), `VigilPaths` (the only place that knows file locations), `JsonlActivityLog`, commands.

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
Schedules are wall-clock windows, so DST transitions follow the clock on the wall (see `ScheduleDstTests`). Pause reasons are evaluated in the order manual, schedule, lock, battery.

## Build, test, publish

```
dotnet build -c Release
dotnet test                                   # runs both test projects (Microsoft Testing Platform)
dotnet publish src/Vigil -c Release -r osx-x64   # Native AOT single binary: src/Vigil/bin/Release/net10.0/osx-x64/publish/vigil
```

The SDK is pinned in `global.json`. `dotnet test` uses Microsoft Testing Platform because `global.json` sets
`test.runner`; xUnit v3 supports it natively.

Never run `vigil run` or `vigil permissions request` on a development machine just to "see it work": they post
input events or show a system prompt. The tests use a fake platform, so no real input is ever posted. `vigil diag`
is read only and safe.

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
2. Read nothing but the OS idle counter. No event taps, no keystroke, mouse, window-title or screen reading. On
   macOS only post-event access may be requested, never Input Monitoring or any listen-event API.
3. Least privilege. Never require root or administrator rights.
4. Data stays local. The activity log stores counters and timestamps only, never content, only in the per-user
   data directory (0700; files 0600), and must stay switchable via `ActivityLog.Enabled`. The IPC socket is
   owner-only.
5. No silent persistence. Never write LaunchAgents, login items, services or any autostart on the user's behalf
   without an explicit user command. Phase 1 has no install or uninstall.
6. Add no NuGet package without the owner's approval; versions are pinned centrally in `Directory.Packages.props`.
7. No secrets in code, config or docs.
