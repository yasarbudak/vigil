namespace Vigil.Platform.Linux.DBus;

/// <summary>Opens a real connection with Tmds.DBus.Protocol, to the address <see cref="DBusAddresses"/> finds.</summary>
internal sealed class DBusConnector : IDBusConnector
{
    public async Task<IDBusConnection> ConnectAsync(DBusBus bus, CancellationToken cancellationToken)
    {
        var address = DBusAddresses.Resolve(bus, Environment.GetEnvironmentVariable, File.Exists);
        if (address is null)
        {
            throw new DBusUnavailableException(bus == DBusBus.Session
                ? "There is no D-Bus session bus: DBUS_SESSION_BUS_ADDRESS has no unix: address and there is no $XDG_RUNTIME_DIR/bus. (Vigil only uses local Unix sockets.)"
                : "There is no D-Bus system bus: DBUS_SYSTEM_BUS_ADDRESS has no unix: address. (Vigil only uses local Unix sockets.)");
        }

        return await TmdsDBusConnection.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
    }
}
