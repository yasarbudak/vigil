using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux;

/// <summary>
/// Whether the screen is locked, from the locked hint of the caller's logind session: the property
/// <c>org.freedesktop.login1.Session.LockedHint</c> (type <c>b</c>) of <c>/org/freedesktop/login1/session/auto</c> on the
/// system bus, read with <c>org.freedesktop.DBus.Properties.Get</c>. <c>auto</c> is one of the convenience objects that the
/// logind documentation says "can only be dereferenced relative to a method caller"
/// (https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.login1.html); logind resolves it to "the
/// caller's own session if they have one, otherwise their user's display session"
/// (<c>manager_get_session_from_creds</c> in src/login/logind-dbus.c), which matters for a systemd user service: it runs
/// in the user manager, not inside the session scope, and has no session of its own.
/// <para>
/// It is a hint: the documentation says the desktop environment sets it (<c>SetLockedHint</c>, "intended to be used by the
/// desktop environment to tell systemd-logind when the session is locked and unlocked"). A desktop that never sets it reads
/// as unlocked while locked; Vigil cannot tell that case apart. Any failure reads as unknown (null).
/// </para>
/// </summary>
internal sealed class LogindSessionLock(DBusClient systemBus)
{
    internal const string Service = "org.freedesktop.login1";
    internal const string SessionPath = "/org/freedesktop/login1/session/auto";
    internal const string SessionInterface = "org.freedesktop.login1.Session";

    internal static DBusCall GetLockedHintCall { get; } = DBusCall.Create(
        Service,
        SessionPath,
        "org.freedesktop.DBus.Properties",
        "Get",
        new DBusString(SessionInterface),
        new DBusString("LockedHint"));

    public bool? IsLocked()
    {
        try
        {
            return Parse(systemBus.Call(GetLockedHintCall));
        }
        catch (DBusFailureException)
        {
            return null;
        }
    }

    /// <summary>The reply of Properties.Get is one variant; a boolean inside is the answer, anything else is unknown.</summary>
    internal static bool? Parse(DBusReply reply) => reply.Body is [DBusVariant { Inner: DBusBool locked }] ? locked.Value : null;
}
