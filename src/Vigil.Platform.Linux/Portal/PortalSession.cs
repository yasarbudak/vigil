using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// A started RemoteDesktop session and the connection it lives on. The portal ties a session to the connection that created
/// it ("a client who started a session vanishing from the D-Bus is equivalent to closing all active sessions"), so this
/// object owns the connection. <see cref="IsClosed"/> turns true when the portal emits <c>Session.Closed</c> (or the
/// connection fails), and the owner then discards the session.
/// </summary>
internal sealed class PortalSession : IDisposable
{
    private readonly IDBusConnection _connection;
    private readonly IDisposable? _closedWatch;
    private volatile bool _closed;

    internal PortalSession(IDBusConnection connection, string path, uint devices, IDisposable? closedWatch)
    {
        _connection = connection;
        Path = path;
        Devices = devices;
        _closedWatch = closedWatch;
    }

    public string Path { get; }

    /// <summary>The devices the user granted: <see cref="PortalProtocol.KeyboardDevice"/> and <see cref="PortalProtocol.PointerDevice"/> bits.</summary>
    public uint Devices { get; }

    public bool IsClosed => _closed;

    internal void MarkClosed() => _closed = true;

    /// <exception cref="DBusFailureException">The call failed; the session is then marked closed so it is not used again.</exception>
    public async Task SendAsync(DBusCall call, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.CallAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (DBusFailureException)
        {
            _closed = true;
            throw;
        }
    }

    /// <summary>Closes the session at the portal (best effort, a short time limit) and drops the connection.</summary>
    public void Dispose()
    {
        _closed = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _connection.CallAsync(PortalProtocol.CloseSession(Path), limit.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DBusFailureException or OperationCanceledException)
        {
            // The session may already be gone; dropping the connection ends it either way.
        }

        _closedWatch?.Dispose();
        _connection.Dispose();
    }
}
