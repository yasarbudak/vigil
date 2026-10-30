using System.Text;

namespace Vigil.Platform.Linux.DBus;

/// <summary>
/// Where the two buses are. Pure: the environment and the file system arrive as functions.
/// The session bus follows sd-bus (<c>bus_set_address_user</c> in systemd's src/libsystemd/sd-bus/sd-bus.c): the address
/// in <c>DBUS_SESSION_BUS_ADDRESS</c>, else <c>unix:path=$XDG_RUNTIME_DIR/bus</c>. A systemd user service does not always have
/// the first variable, which is why the second form matters. Tmds.DBus.Protocol alone would go on to ask the X server for
/// the address when the variable is missing; Vigil resolves it here instead, so that no X11 call hides behind a D-Bus call.
/// The system bus is <c>DBUS_SYSTEM_BUS_ADDRESS</c> or the well-known socket
/// (https://dbus.freedesktop.org/doc/dbus-specification.html#message-bus-types).
/// <para>
/// Only <c>unix:</c> addresses are ever used. The library can also connect over TCP, and a variable that names a
/// <c>tcp:</c> address would make Vigil open a network connection, which it promises never to do: such an entry is skipped,
/// and an address list (entries separated by ";", as the specification allows) is searched for its first <c>unix:</c> one.
/// D-Bus is local inter-process communication over a Unix domain socket, not network access.
/// </para>
/// </summary>
internal static class DBusAddresses
{
    internal const string DefaultSystemAddress = "unix:path=/var/run/dbus/system_bus_socket";

    /// <returns>The address, or null when there is none to try (no variable and no runtime directory).</returns>
    public static string? Resolve(DBusBus bus, Func<string, string?> getenv, Func<string, bool> fileExists)
    {
        if (bus == DBusBus.System)
        {
            return NonEmpty(getenv("DBUS_SYSTEM_BUS_ADDRESS")) is { } system ? FirstUnixAddress(system) : DefaultSystemAddress;
        }

        if (NonEmpty(getenv("DBUS_SESSION_BUS_ADDRESS")) is { } configured)
        {
            return FirstUnixAddress(configured);
        }

        if (NonEmpty(getenv("XDG_RUNTIME_DIR")) is { } runtime && Path.IsPathFullyQualified(runtime))
        {
            var socket = runtime.TrimEnd('/') + "/bus";
            return fileExists(socket) ? "unix:path=" + Escape(socket) : null;
        }

        return null;
    }

    /// <summary>The first entry of a ";"-separated address list that uses the unix transport; null when there is none.</summary>
    internal static string? FirstUnixAddress(string addresses) =>
        addresses.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(entry => entry.StartsWith("unix:", StringComparison.Ordinal));

    /// <summary>
    /// Escapes an address value: bytes outside <c>[-0-9A-Za-z_/.\*]</c> become <c>%xx</c>
    /// (https://dbus.freedesktop.org/doc/dbus-specification.html#addresses).
    /// </summary>
    internal static string Escape(string value)
    {
        var text = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or '*'))
            {
                text.Append(c);
            }
            else
            {
                text.Append('%').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
