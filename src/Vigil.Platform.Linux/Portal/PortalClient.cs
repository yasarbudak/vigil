using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux.Portal;

internal enum PortalInteraction
{
    /// <summary>The daemon: a dialog must not stay on the screen. The request is closed when the portal does not answer in time.</summary>
    Silent,

    /// <summary>`vigil permissions request`: wait for the user.</summary>
    Interactive,
}

internal enum PortalOutcome
{
    Established,

    /// <summary>The user cancelled the dialog (Response 1).</summary>
    Cancelled,

    /// <summary>The portal ended the request some other way (Response 2).</summary>
    Ended,

    /// <summary>No Response within the time limit; the request was closed.</summary>
    TimedOut,

    /// <summary>The portal or the bus cannot be used at all; <see cref="PortalEstablishResult.Message"/> says why.</summary>
    Unavailable,
}

/// <param name="TokenSent">A restore token was passed to SelectDevices, so the portal has used it up whatever happened next.</param>
internal sealed record PortalEstablishResult(
    PortalOutcome Outcome,
    string Message,
    PortalSession? Session = null,
    string? RestoreToken = null,
    bool TokenSent = false);

/// <param name="Step">The limit for each portal call that is answered without the user (CreateSession, SelectDevices).</param>
/// <param name="StartSilent">The limit for Start without a user: with a valid restore token the answer is immediate.</param>
/// <param name="StartInteractive">The limit for Start with the consent dialog on the screen.</param>
internal sealed record PortalTimeouts(TimeSpan Step, TimeSpan StartSilent, TimeSpan StartInteractive)
{
    public static PortalTimeouts Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5));
}

/// <summary>
/// Opens a RemoteDesktop session: CreateSession, SelectDevices (pointer and keyboard, persistence until revoked, the restore
/// token if there is one), Start. Each of the three is a portal "request": the call returns a Request handle at once and the
/// result arrives later in a <c>Response</c> signal on that handle. The signal is subscribed to before the call, using the
/// predictable handle (<see cref="PortalProtocol.RequestPath"/>), so it cannot be missed; the handle the call returns is
/// compared with the expected one and, if it differs (an old portal), subscribed to as well, as the interface documentation
/// recommends.
/// </summary>
internal sealed class PortalClient(IDBusConnector connector, PortalTimeouts timeouts)
{
    public async Task<PortalEstablishResult> EstablishAsync(string? restoreToken, PortalInteraction interaction, CancellationToken cancellationToken)
    {
        IDBusConnection connection;
        try
        {
            connection = await connector.ConnectAsync(DBusBus.Session, cancellationToken).ConfigureAwait(false);
        }
        catch (DBusFailureException ex)
        {
            return Unavailable(ex.Message);
        }

        PortalSession? session = null;
        string? sessionPath = null;
        var tokenSent = false;
        try
        {
            if (await CheckPortalAsync(connection, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return Unavailable(problem);
            }

            var created = await RequestAsync(
                connection,
                token => PortalProtocol.CreateSession(token, PortalProtocol.NewHandleToken()),
                timeouts.Step,
                cancellationToken).ConfigureAwait(false);
            if (Failed(created, "CreateSession", tokenSent) is { } createFailure)
            {
                return createFailure;
            }

            if (!created!.Results.TryGetText("session_handle", out var handle))
            {
                return Unavailable("The portal's CreateSession answer has no session_handle.");
            }

            sessionPath = handle;

            tokenSent = restoreToken is not null;
            var selected = await RequestAsync(
                connection,
                token => PortalProtocol.SelectDevices(handle, token, PortalProtocol.KeyboardDevice | PortalProtocol.PointerDevice, restoreToken),
                timeouts.Step,
                cancellationToken).ConfigureAwait(false);
            if (Failed(selected, "SelectDevices", tokenSent) is { } selectFailure)
            {
                await CloseQuietlyAsync(connection, sessionPath).ConfigureAwait(false);
                return selectFailure;
            }

            var started = await RequestAsync(
                connection,
                token => PortalProtocol.Start(handle, token),
                interaction == PortalInteraction.Interactive ? timeouts.StartInteractive : timeouts.StartSilent,
                cancellationToken).ConfigureAwait(false);
            if (Failed(started, "Start", tokenSent) is { } startFailure)
            {
                await CloseQuietlyAsync(connection, sessionPath).ConfigureAwait(false);
                return startFailure;
            }

            var (devices, newToken) = started!.ReadStartResults();
            PortalSession? opened = null;
            var closedWatch = await connection.WatchSignalAsync(
                PortalProtocol.ClosedMatch(handle),
                _ => opened?.MarkClosed(),
                cancellationToken).ConfigureAwait(false);
            opened = new PortalSession(connection, handle, devices, closedWatch);
            session = opened;
            return new PortalEstablishResult(PortalOutcome.Established, "The portal session is open.", session, newToken, tokenSent);
        }
        catch (DBusFailureException ex)
        {
            if (sessionPath is not null)
            {
                await CloseQuietlyAsync(connection, sessionPath).ConfigureAwait(false);
            }

            return new PortalEstablishResult(PortalOutcome.Unavailable, ex.Message, TokenSent: tokenSent);
        }
        catch (OperationCanceledException)
        {
            if (sessionPath is not null)
            {
                await CloseQuietlyAsync(connection, sessionPath).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (session is null)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>Whether the portal is there, offers RemoteDesktop at version 2 or newer, and offers a keyboard or a pointer.</summary>
    /// <returns>A message for the user when it does not; null when it does.</returns>
    private static async Task<string?> CheckPortalAsync(IDBusConnection connection, CancellationToken cancellationToken)
    {
        uint version;
        try
        {
            version = await ReadUInt32PropertyAsync(connection, "version", cancellationToken).ConfigureAwait(false);
        }
        catch (DBusCallFailedException ex)
        {
            return DescribeMissingPortal(ex);
        }

        if (version < PortalProtocol.MinimumVersion)
        {
            return $"The RemoteDesktop portal is version {version}; Vigil needs version {PortalProtocol.MinimumVersion} or newer, which added the restore token that lets the daemon restart without asking again.";
        }

        try
        {
            var types = await ReadUInt32PropertyAsync(connection, "AvailableDeviceTypes", cancellationToken).ConfigureAwait(false);
            if ((types & (PortalProtocol.KeyboardDevice | PortalProtocol.PointerDevice)) == 0)
            {
                return "The desktop portal backend offers neither keyboard nor pointer control.";
            }
        }
        catch (DBusCallFailedException)
        {
            // The property is documented but optional in practice; the Start result says which devices were granted.
        }

        return null;
    }

    private static async Task<uint> ReadUInt32PropertyAsync(IDBusConnection connection, string name, CancellationToken cancellationToken)
    {
        var reply = await connection.CallAsync(PortalProtocol.GetProperty(name), cancellationToken).ConfigureAwait(false);
        return reply.Body is [DBusVariant { Inner: DBusUInt32 value }]
            ? value.Value
            : throw new DBusCallFailedException("org.freedesktop.DBus.Error.InvalidArgs", $"The portal property {name} is not a uint32.");
    }

    internal static string DescribeMissingPortal(DBusCallFailedException ex) => ex.ErrorName switch
    {
        "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" =>
            "The desktop portal (org.freedesktop.portal.Desktop) is not on the session bus. Install xdg-desktop-portal and the backend of your desktop (xdg-desktop-portal-gnome, -kde, ...).",
        _ => $"The desktop portal has no RemoteDesktop interface ({ex.Message}). Its backend for this desktop may not implement it.",
    };

    /// <summary>
    /// Runs one portal request: subscribe to its Response signal, make the call, wait for the signal. A timeout closes the
    /// request at the portal (which ends a dialog that is showing) and returns null.
    /// </summary>
    private static async Task<PortalResponse?> RequestAsync(
        IDBusConnection connection,
        Func<string, DBusCall> buildCall,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var handleToken = PortalProtocol.NewHandleToken();
        var expected = PortalProtocol.RequestPath(connection.UniqueName ?? throw new DBusUnavailableException("The connection has no unique name."), handleToken);
        var response = new TaskCompletionSource<DBusSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watches = new List<IDisposable>();
        try
        {
            watches.Add(await connection.WatchSignalAsync(PortalProtocol.ResponseMatch(expected), signal => response.TrySetResult(signal), cancellationToken).ConfigureAwait(false));
            var reply = await connection.CallAsync(buildCall(handleToken), cancellationToken).ConfigureAwait(false);
            var actual = reply.Body is [DBusObjectPath handle] ? handle.Value : expected;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                watches.Add(await connection.WatchSignalAsync(PortalProtocol.ResponseMatch(actual), signal => response.TrySetResult(signal), cancellationToken).ConfigureAwait(false));
            }

            try
            {
                var signal = await response.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                return PortalResponse.From(signal)
                    ?? throw new DBusCallFailedException("org.freedesktop.DBus.Error.InvalidArgs", "The portal's Response signal has an unexpected body.");
            }
            catch (TimeoutException)
            {
                await CloseRequestQuietlyAsync(connection, actual).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                // The caller gave up (the daemon is stopping, or the client of a permission request went away): close the
                // request at the portal so that a dialog that is showing goes away, then let the cancellation travel on.
                await CloseRequestQuietlyAsync(connection, actual).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            foreach (var watch in watches)
            {
                watch.Dispose();
            }
        }
    }

    /// <returns>The result to give back when the request did not succeed; null when it did.</returns>
    private static PortalEstablishResult? Failed(PortalResponse? response, string step, bool tokenSent) => response switch
    {
        null => new PortalEstablishResult(
            PortalOutcome.TimedOut,
            $"The portal gave no answer to {step} in time; the request was closed.",
            TokenSent: tokenSent),
        { Code: PortalProtocol.ResponseSuccess } => null,
        { Code: PortalProtocol.ResponseCancelled } => new PortalEstablishResult(
            PortalOutcome.Cancelled,
            $"The consent dialog was cancelled ({step}).",
            TokenSent: tokenSent),
        _ => new PortalEstablishResult(
            PortalOutcome.Ended,
            $"The portal ended the request ({step}, response {response.Code}).",
            TokenSent: tokenSent),
    };

    private static PortalEstablishResult Unavailable(string message) => new(PortalOutcome.Unavailable, message);

    private static async Task CloseQuietlyAsync(IDBusConnection connection, string sessionPath)
    {
        await CallQuietlyAsync(connection, PortalProtocol.CloseSession(sessionPath)).ConfigureAwait(false);
    }

    private static async Task CloseRequestQuietlyAsync(IDBusConnection connection, string requestPath)
    {
        await CallQuietlyAsync(connection, PortalProtocol.CloseRequest(requestPath)).ConfigureAwait(false);
    }

    private static async Task CallQuietlyAsync(IDBusConnection connection, DBusCall call)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.CallAsync(call, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusFailureException or OperationCanceledException)
        {
            // Best effort: the request or session may already be gone.
        }
    }
}
