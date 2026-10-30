using System.Runtime.Versioning;
using Vigil.Core.Abstractions;
using Vigil.Platform.Linux.DBus;
using Vigil.Platform.Linux.Portal;
using Vigil.Platform.Linux.Uinput;
using Vigil.Platform.Linux.X11;

namespace Vigil.Platform.Linux;

/// <summary>What <see cref="LinuxPlatform.Create"/> builds: the Core abstractions plus what the CLI shows about them.</summary>
/// <param name="Summary">One line for <c>vigil status</c>, for example "Wayland session, idle from GNOME Mutter, input through the desktop portal (RemoteDesktop)".</param>
/// <param name="StartupProblem">Set when the daemon can never work in this process (no graphical session in its environment); the daemon then refuses to start with this message.</param>
/// <param name="NotGrantedHint">What to tell the user after the permission request returned false.</param>
/// <param name="Diagnose">Lines for <c>vigil diag</c>. Read only: posts nothing, shows no prompt.</param>
public sealed record LinuxPlatformServices(
    string Summary,
    string? StartupProblem,
    IIdleSource Idle,
    IInputInjector Injector,
    ISessionState Session,
    IPermissionProbe Permissions,
    IPermissionRequester PermissionRequester,
    Func<string> NotGrantedHint,
    Func<IReadOnlyList<LinuxDetail>> Diagnose);

/// <summary>
/// The one place where the session type and the configuration choose the Linux implementations. Idle time and lock state
/// follow the session type (X11: the X server's idle counter; Wayland: the GNOME idle monitor); <c>Linux.Backend</c> chooses
/// how input is injected (see <see cref="InputBackend"/>). Nothing is created that touches the system: the X display, the
/// D-Bus connections, the portal session and the uinput device are all opened by the first call that needs them.
/// </summary>
public static class LinuxPlatform
{
    /// <param name="portalTokenPath">Where the portal restore token is saved (a file in the data directory).</param>
    [SupportedOSPlatform("linux")]
    public static LinuxPlatformServices Create(LinuxOptions options, string portalTokenPath) =>
        Create(options, portalTokenPath, SessionTypeDetector.Detect(Environment.GetEnvironmentVariable));

    [SupportedOSPlatform("linux")]
    internal static LinuxPlatformServices Create(LinuxOptions options, string portalTokenPath, SessionDetection detection)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connector = new DBusConnector();
        var sessionBus = new DBusClient(connector, DBusBus.Session);
        var systemBus = new DBusClient(connector, DBusBus.System);
        var x11 = new X11Session();

        var (idle, idleName) = detection.Type switch
        {
            SessionType.X11 => ((IIdleSource)new X11IdleSource(x11), "the X server (MIT-SCREEN-SAVER)"),
            SessionType.Wayland => (new GnomeIdleSource(sessionBus), "GNOME Mutter (D-Bus org.gnome.Mutter.IdleMonitor)"),
            _ => (new UnavailableIdleSource(NoSessionMessage(detection)), "none"),
        };

        var backend = CreateBackend(options, detection, x11, connector, portalTokenPath);
        var session = new LinuxSessionState(new LogindSessionLock(systemBus), new PowerSupplyReader());

        var summary = detection.Type == SessionType.Unknown
            ? "No graphical session in the environment"
            : $"{detection.Type} session, idle time from {idleName}, input through {backend.Name}";

        return new LinuxPlatformServices(
            summary,
            detection.Type == SessionType.Unknown ? NoSessionMessage(detection) : null,
            idle,
            backend,
            session,
            backend,
            backend,
            () => backend.NotGrantedHint,
            () => Diagnose(options, detection, idleName, backend));
    }

    [SupportedOSPlatform("linux")]
    private static IInputBackend CreateBackend(LinuxOptions options, SessionDetection detection, X11Session x11, IDBusConnector connector, string tokenPath)
    {
        var selected = options.Backend switch
        {
            InputBackend.Auto => detection.Type switch
            {
                SessionType.X11 => InputBackend.X11,
                SessionType.Wayland => InputBackend.Portal,
                _ => (InputBackend?)null,
            },
            var explicitBackend => explicitBackend,
        };

        return selected switch
        {
            InputBackend.X11 => new X11InputBackend(options, x11),
            InputBackend.Portal => new PortalInputBackend(
                new PortalSessionManager(new PortalClient(connector, PortalTimeouts.Default), new PortalTokenStore(tokenPath), options)),
            InputBackend.Uinput => new UinputInputBackend(options),
            _ => new UnavailableInputBackend(NoSessionMessage(detection)),
        };
    }

    private static List<LinuxDetail> Diagnose(LinuxOptions options, SessionDetection detection, string idleName, IInputBackend backend)
    {
        var details = new List<LinuxDetail>
        {
            new("Session type:", detection.Type == SessionType.Unknown ? $"unknown ({detection.Reason})" : $"{detection.Type} ({detection.Reason})"),
            new("Idle source:", idleName),
            new("Input backend:", $"{backend.Name} (Linux.Backend={options.Backend})"),
        };
        if (detection.Type == SessionType.Wayland && options.Backend == InputBackend.X11)
        {
            details.Add(new LinuxDetail("WARNING:", "Linux.Backend=X11 on a Wayland session: XWayland only sees input that reaches X clients, so the idle verification is unreliable."));
        }

        details.AddRange(backend.Diagnose());
        return details;
    }

    private static string NoSessionMessage(SessionDetection detection) =>
        $"There is no graphical session to work with: {detection.Reason}. Start Vigil from inside your desktop session, or (for the systemd unit) make sure the session exports DISPLAY or WAYLAND_DISPLAY and XDG_SESSION_TYPE to the user manager (systemctl --user import-environment).";
}

/// <summary>Idle source for a process without a session; every read says why it cannot work.</summary>
internal sealed class UnavailableIdleSource(string reason) : IIdleSource
{
    public TimeSpan GetIdleTime() => throw new LinuxUnavailableException(reason);
}

/// <summary>Input backend for a process without a session: never granted, every post fails with the reason.</summary>
internal sealed class UnavailableInputBackend(string reason) : IInputBackend
{
    public string Name => "none";

    public string NotGrantedHint => reason;

    public InjectionResult Inject() => InjectionResult.Failure(reason);

    public bool CanPostEvents() => false;

    public bool RequestAccess() => false;

    public IReadOnlyList<LinuxDetail> Diagnose() => [];

    public void Dispose()
    {
    }
}
