namespace Vigil.Platform.Linux.DBus;

/// <summary>Which of the two well-known buses a call goes to.</summary>
internal enum DBusBus
{
    /// <summary>The per-login-session bus: the desktop portal, the GNOME idle monitor.</summary>
    Session,

    /// <summary>The system-wide bus: systemd-logind.</summary>
    System,
}

/// <summary>One method call: where it goes and what it carries. The body signature follows from the values.</summary>
internal sealed record DBusCall(
    string Destination,
    string Path,
    string Interface,
    string Member,
    IReadOnlyList<DBusValue> Body)
{
    public string Signature => DBusValue.SignatureOf(Body);

    public static DBusCall Create(string destination, string path, string @interface, string member, params DBusValue[] body) =>
        new(destination, path, @interface, member, body);
}

/// <summary>The return values of a method call (empty for a method without out arguments).</summary>
internal sealed record DBusReply(IReadOnlyList<DBusValue> Body);

/// <summary>A signal received on the connection.</summary>
internal sealed record DBusSignal(string Path, string Interface, string Member, IReadOnlyList<DBusValue> Body);

/// <summary>Which signals a subscription wants; every field is matched exactly (D-Bus match rule keys).</summary>
/// <param name="Sender">Optional bus name of the emitter; null matches any.</param>
internal sealed record DBusSignalMatch(string? Sender, string Path, string Interface, string Member);

/// <summary>Common base of the two ways a D-Bus operation fails, so callers can catch either.</summary>
internal abstract class DBusFailureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The peer answered with an error reply (for example <c>org.freedesktop.DBus.Error.ServiceUnknown</c>).</summary>
internal sealed class DBusCallFailedException(string errorName, string message)
    : DBusFailureException($"{errorName}: {message}")
{
    public string ErrorName { get; } = errorName;
}

/// <summary>There is no usable connection: no bus address, the connect failed, the connection dropped or a call timed out.</summary>
internal sealed class DBusUnavailableException(string message, Exception? inner = null) : DBusFailureException(message, inner);

internal interface IDBusConnection : IDisposable
{
    /// <summary>The unique name the bus assigned to this connection (":1.42"); null if not connected.</summary>
    string? UniqueName { get; }

    /// <exception cref="DBusCallFailedException">The peer replied with an error.</exception>
    /// <exception cref="DBusUnavailableException">The connection is not usable.</exception>
    Task<DBusReply> CallAsync(DBusCall call, CancellationToken cancellationToken);

    /// <summary>Starts delivering matching signals to <paramref name="handler"/>; dispose the result to stop.</summary>
    Task<IDisposable> WatchSignalAsync(DBusSignalMatch match, Action<DBusSignal> handler, CancellationToken cancellationToken);
}

/// <summary>Opens connections. Behind an interface so tests never touch a real bus.</summary>
internal interface IDBusConnector
{
    /// <exception cref="DBusUnavailableException">No address for the bus, or the connection failed.</exception>
    Task<IDBusConnection> ConnectAsync(DBusBus bus, CancellationToken cancellationToken);
}
