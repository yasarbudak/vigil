using Vigil.Core.Abstractions;
using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux;

/// <summary>
/// The idle time of a GNOME session (Wayland or X11) from the compositor's own idle monitor:
/// <c>org.gnome.Mutter.IdleMonitor.GetIdletime() -> t</c> on <c>/org/gnome/Mutter/IdleMonitor/Core</c>, owned by
/// <c>org.gnome.Mutter.IdleMonitor</c> on the session bus. The reply is a uint64 in milliseconds: in mutter,
/// <c>meta_idle_monitor_get_idletime</c> returns <c>(g_get_monotonic_time () - last_event_time) / 1000</c> with the
/// comment "idle time, in milliseconds", and the reset on every input event includes events from a remote desktop
/// session. https://gitlab.gnome.org/GNOME/mutter/-/blob/main/data/dbus-interfaces/org.gnome.Mutter.IdleMonitor.xml
/// and src/backends/meta-idle-manager.c, meta-idle-monitor.c. It is a GNOME-internal interface (the XML says it is used by
/// gnome-desktop), not a freedesktop standard, which is why it is used only after the call has shown that it exists.
/// <para>
/// KDE Plasma is not covered on Wayland: <c>org.freedesktop.ScreenSaver.GetSessionIdleTime</c> exists in kscreenlocker's
/// interface (dbus/org.freedesktop.ScreenSaver.xml), but its implementation (interface.cpp) replies
/// <c>org.freedesktop.DBus.Error.NotSupported</c> when <c>KWindowSystem::isPlatformWayland()</c> is true. Other Wayland
/// compositors expose idle time only through the <c>ext-idle-notify-v1</c> protocol, which is not a query.
/// </para>
/// </summary>
internal sealed class GnomeIdleSource(DBusClient bus) : IIdleSource
{
    internal const string Service = "org.gnome.Mutter.IdleMonitor";
    internal const string ObjectPath = "/org/gnome/Mutter/IdleMonitor/Core";

    internal static DBusCall GetIdletimeCall { get; } = DBusCall.Create(Service, ObjectPath, Service, "GetIdletime");

    /// <exception cref="LinuxUnavailableException">There is no GNOME idle monitor on the session bus; the message says why.</exception>
    public TimeSpan GetIdleTime()
    {
        try
        {
            return Parse(bus.Call(GetIdletimeCall));
        }
        catch (DBusCallFailedException ex)
        {
            throw new LinuxUnavailableException(Describe(ex), ex);
        }
        catch (DBusUnavailableException ex)
        {
            throw new LinuxUnavailableException($"Cannot read the idle time: {ex.Message}", ex);
        }
    }

    /// <summary>The reply is exactly one uint64 of milliseconds.</summary>
    internal static TimeSpan Parse(DBusReply reply)
    {
        if (reply.Body is not [DBusUInt64 milliseconds])
        {
            throw new LinuxUnavailableException($"The GNOME idle monitor answered with '{DBusValue.SignatureOf(reply.Body)}' instead of one uint64.");
        }

        return IdleMilliseconds.ToTimeSpan(milliseconds.Value);
    }

    internal static string Describe(DBusCallFailedException ex) => ex.ErrorName switch
    {
        "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" =>
            "This Wayland session has no idle source Vigil can read: the GNOME idle monitor (org.gnome.Mutter.IdleMonitor) is not on the session bus. "
            + "Only GNOME (Mutter) is supported on Wayland; KDE Plasma refuses GetSessionIdleTime on Wayland and other compositors have no way to query idle time.",
        _ => $"The GNOME idle monitor refused the call: {ex.Message}",
    };
}
