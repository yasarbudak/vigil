using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.X11;

/// <summary>
/// Reads the idle time of the X server (libXss, <c>XScreenSaverQueryInfo</c>): milliseconds since the last input event,
/// including the ones posted with XTest. This is the only thing Vigil reads about user activity. In a Wayland session an
/// X server (XWayland) only sees the input that reaches X clients, so this source is for X11 sessions.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11IdleSource(X11Session session) : IIdleSource
{
    /// <exception cref="LinuxUnavailableException">There is no X server to ask; the worker logs it and tries again at the next poll.</exception>
    public TimeSpan GetIdleTime() => session.GetIdleTime();
}
