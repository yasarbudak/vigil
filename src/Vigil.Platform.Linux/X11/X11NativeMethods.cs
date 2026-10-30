using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigil.Platform.Linux.X11;

/// <summary>
/// XScreenSaverInfo (X11/extensions/scrnsaver.h): Window window; int state; int kind; unsigned long til_or_since;
/// unsigned long idle; unsigned long eventMask. On the LP64 ABI of linux-x64 and linux-arm64 Window (XID) and
/// unsigned long are 8 bytes, so the sizes and offsets are 0, 8, 12, 16, 24, 32 and 40 in total
/// (<c>X11LayoutTests</c> pins them). Only <see cref="Idle"/> is read; the structure is allocated by
/// XScreenSaverAllocInfo, never by Vigil, so a different size in a future library cannot be overrun.
/// https://gitlab.freedesktop.org/xorg/lib/libxscrnsaver/-/blob/master/include/X11/extensions/scrnsaver.h
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XScreenSaverInfo
{
    public nuint Window;
    public int State;
    public int Kind;
    public nuint TilOrSince;

    /// <summary>Milliseconds since the last user input (libXss man page, XScreenSaverInfo: "idle ... milliseconds").</summary>
    public nuint Idle;
    public nuint EventMask;
}

/// <summary>
/// XErrorEvent (X11/Xlib.h): int type; Display *display; XID resourceid; unsigned long serial; unsigned char error_code,
/// request_code, minor_code. Offsets on LP64: 0, 8, 16, 24, 32, 33, 34.
/// https://gitlab.freedesktop.org/xorg/lib/libx11/-/blob/master/include/X11/Xlib.h
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XErrorEvent
{
    public int Type;
    public nint Display;
    public nuint ResourceId;
    public nuint Serial;
    public byte ErrorCode;
    public byte RequestCode;
    public byte MinorCode;
}

/// <summary>
/// Every import below was checked against the header named next to it (xorg master, the files libX11, libXss and libXtst
/// install). Bool and Status are <c>int</c> in Xlib (X11/Xlib.h: <c>typedef int Bool</c>, <c>typedef int Status</c>);
/// Window, Drawable, KeySym and the delay are 8 bytes on LP64, which are the two Linux targets. The libraries are loaded
/// by name at the first call, so a missing library is detected beforehand by <see cref="X11Libraries"/> and never
/// reaches these calls. Only idle reading and posting are used: no event selection, no grabs, no reading of keys or
/// pointer position.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class X11NativeMethods
{
    // X11/Xlib.h: Display *XOpenDisplay(_Xconst char *display_name). NULL means the DISPLAY variable.
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial nint XOpenDisplay(nint displayName);

    // X11/Xlib.h: int XCloseDisplay(Display *display)
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial int XCloseDisplay(nint display);

    // X11/Xlib.h: Window XDefaultRootWindow(Display *display)
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial nuint XDefaultRootWindow(nint display);

    // X11/Xlib.h: KeyCode XKeysymToKeycode(Display *display, KeySym keysym). KeyCode is unsigned char (X11/X.h); 0 = no such key.
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial byte XKeysymToKeycode(nint display, nuint keysym);

    // X11/Xlib.h: int XSync(Display *display, Bool discard). Flushes the request buffer (like XFlush) and waits until the
    // server has processed every request, so protocol errors reach the error handler before it returns.
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial int XSync(nint display, int discard);

    // X11/Xlib.h: int XFree(void *data). Releases what XScreenSaverAllocInfo returned (libXss man page).
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial int XFree(nint data);

    // X11/Xlib.h: XErrorHandler XSetErrorHandler(XErrorHandler handler); typedef int (*XErrorHandler)(Display *, XErrorEvent *).
    // Without a handler Xlib prints the error and exits the process.
    [LibraryImport(X11Libraries.Xlib)]
    internal static partial delegate* unmanaged<nint, XErrorEvent*, int> XSetErrorHandler(delegate* unmanaged<nint, XErrorEvent*, int> handler);

    // X11/extensions/scrnsaver.h: Bool XScreenSaverQueryExtension(Display *, int *event_base, int *error_base)
    [LibraryImport(X11Libraries.Xss)]
    internal static partial int XScreenSaverQueryExtension(nint display, out int eventBase, out int errorBase);

    // X11/extensions/scrnsaver.h: XScreenSaverInfo *XScreenSaverAllocInfo(void); release with XFree.
    [LibraryImport(X11Libraries.Xss)]
    internal static partial nint XScreenSaverAllocInfo();

    // X11/extensions/scrnsaver.h: Status XScreenSaverQueryInfo(Display *, Drawable, XScreenSaverInfo *). 0 = failure.
    [LibraryImport(X11Libraries.Xss)]
    internal static partial int XScreenSaverQueryInfo(nint display, nuint drawable, nint info);

    // X11/extensions/XTest.h: Bool XTestQueryExtension(Display *, int *event_base, int *error_base, int *major, int *minor)
    [LibraryImport(X11Libraries.Xtst)]
    internal static partial int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);

    // X11/extensions/XTest.h: int XTestFakeKeyEvent(Display *, unsigned int keycode, Bool is_press, unsigned long delay).
    // The libXtst source (src/XTest.c) returns 1 once the request is queued and 0 when the extension is missing; a delay of 0 is "now".
    [LibraryImport(X11Libraries.Xtst)]
    internal static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);

    // X11/extensions/XTest.h: int XTestFakeRelativeMotionEvent(Display *, int x, int y, unsigned long delay). Same return values.
    [LibraryImport(X11Libraries.Xtst)]
    internal static partial int XTestFakeRelativeMotionEvent(nint display, int x, int y, nuint delay);
}
