using System.Security.Cryptography;
using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// The names, constants and messages of the xdg-desktop-portal RemoteDesktop interface that Vigil uses. Pure: it only builds
/// <see cref="DBusCall"/> values and reads <see cref="DBusSignal"/> values. Every method, argument, option key and value
/// below is taken from the portal's own interface descriptions (xdg-desktop-portal, <c>data/</c>):
/// https://github.com/flatpak/xdg-desktop-portal/blob/main/data/org.freedesktop.portal.RemoteDesktop.xml (version 2 of the
/// interface),
/// https://github.com/flatpak/xdg-desktop-portal/blob/main/data/org.freedesktop.portal.Request.xml and
/// https://github.com/flatpak/xdg-desktop-portal/blob/main/data/org.freedesktop.portal.Session.xml.
/// The bus name and object path (<c>org.freedesktop.portal.Desktop</c>, <c>/org/freedesktop/portal/desktop</c>) are the ones the
/// portal's own test suite connects to (tests/xdp_utils.py). The empty <c>parent_window</c> is the documented value "if you
/// don't have a suitable handle" (doc/window-identifiers.rst).
/// </summary>
internal static class PortalProtocol
{
    internal const string Service = "org.freedesktop.portal.Desktop";
    internal const string ObjectPath = "/org/freedesktop/portal/desktop";
    internal const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    internal const string RequestInterface = "org.freedesktop.portal.Request";
    internal const string SessionInterface = "org.freedesktop.portal.Session";

    /// <summary>RemoteDesktop <c>AvailableDeviceTypes</c> and <c>SelectDevices</c> <c>types</c>: 1 KEYBOARD.</summary>
    internal const uint KeyboardDevice = 1;

    /// <summary>RemoteDesktop <c>AvailableDeviceTypes</c> and <c>SelectDevices</c> <c>types</c>: 2 POINTER.</summary>
    internal const uint PointerDevice = 2;

    /// <summary>SelectDevices <c>persist_mode</c> 2: "Permissions persist until explicitly revoked".</summary>
    internal const uint PersistUntilRevoked = 2;

    /// <summary><c>restore_token</c> and <c>persist_mode</c> "were added in version 2 of this interface".</summary>
    internal const uint MinimumVersion = 2;

    /// <summary>NotifyKeyboardKeysym <c>state</c>: 0 Released.</summary>
    internal const uint KeyReleased = 0;

    /// <summary>Request.Response <c>response</c>: 0 success, 1 the user cancelled, 2 ended in some other way.</summary>
    internal const uint ResponseSuccess = 0;

    internal const uint ResponseCancelled = 1;

    /// <summary>
    /// The path of a Request: <c>/org/freedesktop/portal/desktop/request/SENDER/TOKEN</c>, where SENDER is the caller's
    /// unique name "with the initial ':' removed and all '.' replaced by '_'" (Request interface description). Known before the
    /// call, so the Response signal can be subscribed to first and cannot be missed.
    /// </summary>
    internal static string RequestPath(string uniqueName, string handleToken) =>
        $"{ObjectPath}/request/{SenderPart(uniqueName)}/{handleToken}";

    internal static string SenderPart(string uniqueName) => uniqueName.TrimStart(':').Replace('.', '_');

    /// <summary>A token must be "a valid object path element": ASCII letters, digits and underscore.</summary>
    internal static bool IsValidHandleToken(string token) =>
        token.Length > 0 && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// "Unique and not guessable", with a per-library prefix (Request interface description): 128 random bits in hex.
    /// </summary>
    internal static string NewHandleToken() => "vigil_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    internal static DBusSignalMatch ResponseMatch(string requestPath) => new(null, requestPath, RequestInterface, "Response");

    internal static DBusSignalMatch ClosedMatch(string sessionPath) => new(null, sessionPath, SessionInterface, "Closed");

    internal static DBusCall GetProperty(string name) => DBusCall.Create(
        Service,
        ObjectPath,
        "org.freedesktop.DBus.Properties",
        "Get",
        new DBusString(RemoteDesktop),
        new DBusString(name));

    internal static DBusCall CreateSession(string handleToken, string sessionHandleToken) => Call(
        "CreateSession",
        DBusDict.Of(
            ("handle_token", new DBusString(handleToken)),
            ("session_handle_token", new DBusString(sessionHandleToken))));

    /// <param name="restoreToken">The token of a previous grant; "If the stored session cannot be restored, this value is ignored and the user will be prompted normally."</param>
    internal static DBusCall SelectDevices(string sessionPath, string handleToken, uint types, string? restoreToken)
    {
        var options = new List<KeyValuePair<string, DBusValue>>
        {
            new("handle_token", new DBusString(handleToken)),
            new("types", new DBusUInt32(types)),
            new("persist_mode", new DBusUInt32(PersistUntilRevoked)),
        };
        if (restoreToken is not null)
        {
            options.Add(new("restore_token", new DBusString(restoreToken)));
        }

        return Call("SelectDevices", new DBusObjectPath(sessionPath), new DBusDict(options));
    }

    internal static DBusCall Start(string sessionPath, string handleToken) => Call(
        "Start",
        new DBusObjectPath(sessionPath),
        new DBusString(string.Empty),
        DBusDict.Of(("handle_token", new DBusString(handleToken))));

    /// <summary>NotifyPointerMotion(session_handle o, options a{sv}, dx d, dy d): "a new relative pointer motion event".</summary>
    internal static DBusCall NotifyPointerMotion(string sessionPath, double dx, double dy) => Call(
        "NotifyPointerMotion",
        new DBusObjectPath(sessionPath),
        DBusDict.Empty,
        new DBusDouble(dx),
        new DBusDouble(dy));

    /// <summary>NotifyKeyboardKeysym(session_handle o, options a{sv}, keysym i, state u); state 0 is Released.</summary>
    internal static DBusCall NotifyKeyboardKeysym(string sessionPath, int keysym, uint state) => Call(
        "NotifyKeyboardKeysym",
        new DBusObjectPath(sessionPath),
        DBusDict.Empty,
        new DBusInt32(keysym),
        new DBusUInt32(state));

    /// <summary>Request.Close: "Closes the portal request ... and ends all related user interaction (dialogs, etc)."</summary>
    internal static DBusCall CloseRequest(string requestPath) =>
        DBusCall.Create(Service, requestPath, RequestInterface, "Close");

    internal static DBusCall CloseSession(string sessionPath) =>
        DBusCall.Create(Service, sessionPath, SessionInterface, "Close");

    private static DBusCall Call(string member, params DBusValue[] body) =>
        DBusCall.Create(Service, ObjectPath, RemoteDesktop, member, body);
}
