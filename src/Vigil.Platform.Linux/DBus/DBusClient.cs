namespace Vigil.Platform.Linux.DBus;

/// <summary>
/// Synchronous method calls over one cached connection to one bus, for the code that Vigil's poll loop calls (the idle
/// reader and the lock reader). Connects on first use, gives every call a time limit, and drops the connection after a
/// transport failure so that the next call connects again; an error reply from the peer leaves the connection alone.
/// Calls are serialised, which is all the poll loop needs.
/// </summary>
internal sealed class DBusClient(IDBusConnector connector, DBusBus bus, TimeSpan? callTimeout = null) : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private IDBusConnection? _connection;

    /// <exception cref="DBusCallFailedException">The peer replied with an error.</exception>
    /// <exception cref="DBusUnavailableException">The bus cannot be reached or did not answer in time.</exception>
    public DBusReply Call(DBusCall call)
    {
        lock (_gate)
        {
            using var limit = new CancellationTokenSource(callTimeout ?? DefaultTimeout);
            try
            {
                _connection ??= connector.ConnectAsync(bus, limit.Token).GetAwaiter().GetResult();
                return _connection.CallAsync(call, limit.Token).GetAwaiter().GetResult();
            }
            catch (DBusUnavailableException)
            {
                Drop();
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Drop();
                throw new DBusUnavailableException($"The {bus.ToString().ToLowerInvariant()} bus did not answer {call.Interface}.{call.Member} in time.", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Drop();
        }
    }

    private void Drop()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
