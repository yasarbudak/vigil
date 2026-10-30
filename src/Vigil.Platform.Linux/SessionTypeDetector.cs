namespace Vigil.Platform.Linux;

public enum SessionType
{
    /// <summary>No graphical session could be recognised from the environment.</summary>
    Unknown,

    X11,

    Wayland,
}

/// <param name="Reason">Which variable decided, or why nothing did; shown by <c>vigil diag</c>.</param>
public readonly record struct SessionDetection(SessionType Type, string Reason);

/// <summary>
/// Tells X11 from Wayland from the environment. Pure: the environment arrives as a function. Decision order:
/// <list type="number">
/// <item><c>XDG_SESSION_TYPE</c> when it is <c>wayland</c> or <c>x11</c>. pam_systemd sets it from the type logind
/// recorded for the session (https://www.freedesktop.org/software/systemd/man/latest/pam_systemd.html, where the other
/// values are <c>unspecified</c>, <c>tty</c>, <c>mir</c> and <c>web</c>). It is first because a Wayland session also has
/// <c>DISPLAY</c> (XWayland), so <c>DISPLAY</c> alone would call it X11.</item>
/// <item><c>WAYLAND_DISPLAY</c> when set: <c>wl_display_connect</c> reads it to find the compositor
/// (https://gitlab.freedesktop.org/wayland/wayland/-/blob/main/src/wayland-client.c). It comes before <c>DISPLAY</c> for the
/// XWayland reason above.</item>
/// <item><c>DISPLAY</c> when set: <c>XOpenDisplay(NULL)</c> reads it
/// (https://www.x.org/releases/current/doc/libX11/libX11/libX11.html).</item>
/// <item>Otherwise <see cref="SessionType.Unknown"/>. This is what a systemd user service sees when the session did not
/// export its environment to the user manager.</item>
/// </list>
/// </summary>
public static class SessionTypeDetector
{
    public static SessionDetection Detect(Func<string, string?> getenv)
    {
        ArgumentNullException.ThrowIfNull(getenv);

        var sessionType = Value(getenv, "XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionDetection(SessionType.Wayland, "XDG_SESSION_TYPE=wayland");
        }

        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionDetection(SessionType.X11, "XDG_SESSION_TYPE=x11");
        }

        if (Value(getenv, "WAYLAND_DISPLAY") is { } wayland)
        {
            return new SessionDetection(SessionType.Wayland, $"WAYLAND_DISPLAY={wayland}");
        }

        if (Value(getenv, "DISPLAY") is { } display)
        {
            return new SessionDetection(SessionType.X11, $"DISPLAY={display}");
        }

        return new SessionDetection(
            SessionType.Unknown,
            "none of XDG_SESSION_TYPE (x11 or wayland), WAYLAND_DISPLAY and DISPLAY is set: not a graphical session, or this process lacks its environment");
    }

    private static string? Value(Func<string, string?> getenv, string name) =>
        getenv(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
