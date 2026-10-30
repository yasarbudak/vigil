using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.X11;

/// <summary>
/// One connection to the X server, shared by the idle reader, the injector and the permission probe. Opened on first use
/// and kept; if the display cannot be opened (no <c>DISPLAY</c>, no server) every call says so, and the next call tries
/// again. Xlib is not thread safe without XInitThreads, so every call holds one lock. If the X server goes away while
/// connected, Xlib's fatal I/O error handling ends the process (the handler may not return); under the systemd unit that
/// is the end of the graphical session anyway (<c>PartOf=graphical-session.target</c>).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class X11Session : IDisposable
{
    private readonly object _gate = new();
    private nint _display;
    private bool? _screenSaverAvailable;
    private bool? _xtestAvailable;
    private static int s_lastError;

    /// <summary>The XErrorEvent of the most recent protocol error, packed as error_code | request_code << 8 | minor_code << 16, or 0.</summary>
    private static int LastError
    {
        get => Volatile.Read(ref s_lastError);
        set => Volatile.Write(ref s_lastError, value);
    }

    /// <exception cref="LinuxUnavailableException">The display cannot be opened, a library is missing or the extension is not there.</exception>
    public TimeSpan GetIdleTime()
    {
        lock (_gate)
        {
            var display = Open(X11Libraries.Xlib, X11Libraries.Xss);
            _screenSaverAvailable ??= X11NativeMethods.XScreenSaverQueryExtension(display, out _, out _) != 0;
            if (_screenSaverAvailable != true)
            {
                throw new LinuxUnavailableException("The X server has no MIT-SCREEN-SAVER extension, so Vigil cannot read the idle time.");
            }

            var info = X11NativeMethods.XScreenSaverAllocInfo();
            if (info == nint.Zero)
            {
                throw new LinuxUnavailableException("XScreenSaverAllocInfo returned no memory.");
            }

            try
            {
                if (X11NativeMethods.XScreenSaverQueryInfo(display, X11NativeMethods.XDefaultRootWindow(display), info) == 0)
                {
                    throw new LinuxUnavailableException("XScreenSaverQueryInfo failed.");
                }

                var idle = ((XScreenSaverInfo*)info)->Idle;
                return IdleMilliseconds.ToTimeSpan(idle);
            }
            finally
            {
                _ = X11NativeMethods.XFree(info);
            }
        }
    }

    /// <summary>Whether input can be posted with XTest right now, and why not when it cannot.</summary>
    public bool CanPost(out string? problem)
    {
        lock (_gate)
        {
            try
            {
                var display = Open(X11Libraries.Xlib, X11Libraries.Xtst);
                _xtestAvailable ??= X11NativeMethods.XTestQueryExtension(display, out _, out _, out _, out _) != 0;
                problem = _xtestAvailable == true ? null : "The X server has no XTEST extension, so Vigil cannot post input.";
                return _xtestAvailable == true;
            }
            catch (LinuxUnavailableException ex)
            {
                problem = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Posts a relative motion of +1 px and one back of -1 px: zero net movement (see <see cref="InjectionStrategy.MouseNudge"/>).</summary>
    public InjectionResult NudgeMouse()
    {
        lock (_gate)
        {
            if (!TryOpenForPosting(out var display, out var failure))
            {
                return failure;
            }

            LastError = 0;
            var queued = X11NativeMethods.XTestFakeRelativeMotionEvent(display, 1, 0, 0) != 0
                && X11NativeMethods.XTestFakeRelativeMotionEvent(display, -1, 0, 0) != 0;
            return Finish(display, queued, "pointer motion");
        }
    }

    /// <summary>
    /// Posts only the key-up of F15. The X server does not drop the release of a key that is not down: in dix/getevents.c
    /// GetKeyboardEvents only a repeated press is dropped (and that only when the key does not autorepeat).
    /// </summary>
    public InjectionResult ReleaseF15()
    {
        lock (_gate)
        {
            if (!TryOpenForPosting(out var display, out var failure))
            {
                return failure;
            }

            var keycode = X11NativeMethods.XKeysymToKeycode(display, (nuint)Keysyms.F15);
            if (keycode == 0)
            {
                return InjectionResult.Failure("The current keyboard layout has no F15 key (XKeysymToKeycode returned 0). Set Linux.Strategy to MouseNudge.");
            }

            LastError = 0;
            var queued = X11NativeMethods.XTestFakeKeyEvent(display, keycode, 0, 0) != 0;
            return Finish(display, queued, "F15 key-up");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_display != nint.Zero)
            {
                _ = X11NativeMethods.XCloseDisplay(_display);
                _display = nint.Zero;
            }
        }
    }

    private bool TryOpenForPosting(out nint display, out InjectionResult failure)
    {
        display = nint.Zero;
        failure = default;
        try
        {
            display = Open(X11Libraries.Xlib, X11Libraries.Xtst);
        }
        catch (LinuxUnavailableException ex)
        {
            failure = InjectionResult.Failure(ex.Message);
            return false;
        }

        _xtestAvailable ??= X11NativeMethods.XTestQueryExtension(display, out _, out _, out _, out _) != 0;
        if (_xtestAvailable != true)
        {
            failure = InjectionResult.Failure("The X server has no XTEST extension, so Vigil cannot post input.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// XSync (which includes the flush) makes the server process the requests now, so a protocol error is known before this
    /// returns and the result says what happened instead of reporting a post that was refused later.
    /// </summary>
    private static InjectionResult Finish(nint display, bool queued, string what)
    {
        if (!queued)
        {
            return InjectionResult.Failure($"XTest did not accept the {what} request.");
        }

        _ = X11NativeMethods.XSync(display, 0);
        var error = LastError;
        return error == 0
            ? InjectionResult.Success
            : InjectionResult.Failure($"The X server refused the {what} (error code {error & 0xff}, request {(error >> 8) & 0xff}, minor {(error >> 16) & 0xff}).");
    }

    private nint Open(params string[] libraries)
    {
        if (X11Libraries.CheckAvailable(libraries) is { } missing)
        {
            throw new LinuxUnavailableException(missing);
        }

        if (_display != nint.Zero)
        {
            return _display;
        }

        // Without a handler Xlib prints a protocol error and exits the process; this one only records it.
        // (Process-wide state: Xlib has one error handler per process.)
        _ = X11NativeMethods.XSetErrorHandler(&OnXError);

        var display = X11NativeMethods.XOpenDisplay(nint.Zero);
        if (display == nint.Zero)
        {
            var name = Environment.GetEnvironmentVariable("DISPLAY");
            throw new LinuxUnavailableException(string.IsNullOrWhiteSpace(name)
                ? "DISPLAY is not set, so there is no X server to talk to."
                : $"Cannot open the X display '{name}'.");
        }

        _display = display;
        return display;
    }

    [UnmanagedCallersOnly]
    private static int OnXError(nint display, XErrorEvent* error)
    {
        var packed = error->ErrorCode | (error->RequestCode << 8) | (error->MinorCode << 16);
        LastError = packed == 0 ? 1 : packed;
        return 0;
    }
}
