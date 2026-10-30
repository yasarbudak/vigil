using Vigil.Core.Abstractions;
using Vigil.Core.Presence;

namespace Vigil.Ipc;

internal enum IpcCommand
{
    Status,
    Pause,
    PauseUntilTomorrow,
    Resume,

    /// <summary>Asks the daemon to stop. Sent by <c>vigil uninstall</c> on Windows, where there is no service manager to do it.</summary>
    Shutdown,

    /// <summary>
    /// Asks the daemon to run the OS consent flow itself (Linux desktop portal) and answers when the user has answered, which can
    /// take minutes (see <see cref="IpcTimeouts"/>). Sent by <c>vigil permissions request</c>; the only command that can cause a prompt.
    /// </summary>
    RequestPermission,

    /// <summary>
    /// Asks the daemon to read config.json again and apply it. The daemon validates the file first: when it is not valid nothing
    /// changes and the answer carries the error. Sent by the tray after it saved the settings.
    /// </summary>
    ReloadConfig,
}

/// <summary>How long each side of the socket waits. Every command is quick except <see cref="IpcCommand.RequestPermission"/>, which waits for a person.</summary>
internal static class IpcTimeouts
{
    /// <summary>For every command but <see cref="IpcCommand.RequestPermission"/>: the client's whole exchange, and the server's read and write of one connection.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What the client waits for <see cref="IpcCommand.RequestPermission"/>. The portal gives the user 5 minutes
    /// (<c>PortalTimeouts.StartInteractive</c>) after two quick steps of 10 seconds each; the rest is margin, so that the daemon's own
    /// answer, even a "timed out", always arrives before the client gives up.
    /// </summary>
    public static readonly TimeSpan Consent = TimeSpan.FromMinutes(6);

    /// <summary>The daemon ends a consent request that is still waiting after this long (<see cref="Consent"/> minus 30 seconds), so it answers before the client stops waiting.</summary>
    public static readonly TimeSpan ConsentDaemonLimit = Consent - TimeSpan.FromSeconds(30);

    /// <summary>True for a command whose handler waits for a person, so the server watches whether its client is still there.</summary>
    public static bool IsLongRunning(IpcCommand command) => command == IpcCommand.RequestPermission;

    public static TimeSpan For(IpcCommand command) => command == IpcCommand.RequestPermission ? Consent : Default;
}

/// <summary>One request per connection, sent as a single JSON line.</summary>
internal sealed record IpcRequest(IpcCommand Command, TimeSpan? Duration = null);

/// <summary>One response per connection, sent as a single JSON line.</summary>
/// <param name="Platform">How the platform works in this daemon (the Linux session type and backends); null when there is nothing to add.</param>
/// <param name="Message">The text for the user when <see cref="Ok"/> is true and there is no status to show (a granted permission request).</param>
/// <param name="Consent">How a <see cref="IpcCommand.RequestPermission"/> ended; null for every other command and when the daemon refused the command.</param>
/// <param name="RestartRequired">
/// For <see cref="IpcCommand.ReloadConfig"/>: the keys of the new configuration that the running daemon cannot take over and that
/// apply only after it is restarted (for example <c>Linux.Backend</c>); null or empty when everything was applied.
/// </param>
internal sealed record IpcResponse(
    bool Ok,
    string? Error = null,
    PresenceStatus? Status = null,
    string? Platform = null,
    string? Message = null,
    ConsentOutcome? Consent = null,
    IReadOnlyList<string>? RestartRequired = null);

internal interface IIpcHandler
{
    IpcResponse Handle(IpcRequest request);

    /// <summary>
    /// What the server calls. A handler with a command that waits for a person overrides it and awaits; the server applies no time
    /// limit to this call (only <paramref name="cancellationToken"/>, the daemon stopping), so the handler bounds the wait itself.
    /// By default it is the quick <see cref="Handle"/>.
    /// </summary>
    Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken) => Task.FromResult(Handle(request));

    /// <summary>
    /// Called by the server after the response to <paramref name="request"/> has been written, for effects that must
    /// not happen before the client has its answer (stopping the daemon would cut the connection). Does nothing by default.
    /// </summary>
    void ResponseSent(IpcRequest request)
    {
    }
}
