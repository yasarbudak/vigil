using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux.Portal;

/// <summary>A <c>org.freedesktop.portal.Request.Response</c> signal: <c>(u response, a{sv} results)</c>.</summary>
internal sealed record PortalResponse(uint Code, DBusDict Results)
{
    internal static PortalResponse? From(DBusSignal signal) =>
        signal.Body is [DBusUInt32 code, DBusDict results] ? new PortalResponse(code.Value, results) : null;

    /// <summary>
    /// The result of <c>Start</c>: <c>devices</c> (u), "a bitmask of the devices selected by the user", and the optional
    /// <c>restore_token</c> (s), "a single use token that can later be used to restore a session".
    /// </summary>
    internal (uint Devices, string? RestoreToken) ReadStartResults() =>
        (Results.TryGetUInt32("devices", out var devices) ? devices : 0,
            Results.TryGetString("restore_token", out var token) && PortalTokenStore.IsValid(token) ? token : null);
}
