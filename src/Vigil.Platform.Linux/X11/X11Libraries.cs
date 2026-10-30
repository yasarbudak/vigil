using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Vigil.Platform.Linux.X11;

/// <summary>
/// The three X11 client libraries, loaded by name when the first call needs them. They are not linked at build time, and
/// a machine without one (a server without a desktop, a Wayland-only system) must give a clear "unavailable" and not a
/// crash, so every use is preceded by <see cref="Missing"/>. The names are the sonames the Debian, Ubuntu and Fedora
/// packages install (libx11-6, libxss1, libxtst6 and the same libraries elsewhere).
/// </summary>
internal static class X11Libraries
{
    internal const string Xlib = "libX11.so.6";
    internal const string Xss = "libXss.so.1";
    internal const string Xtst = "libXtst.so.6";

    /// <returns>The first of <paramref name="sonames"/> that cannot be loaded, or null when all can.</returns>
    internal static string? Missing(IEnumerable<string> sonames, Func<string, bool> canLoad)
    {
        foreach (var soname in sonames)
        {
            if (!canLoad(soname))
            {
                return soname;
            }
        }

        return null;
    }

    private static readonly ConcurrentDictionary<string, bool> s_loaded = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads each library to see whether it is there; the handle stays loaded on purpose, so the import that follows finds it
    /// already loaded. A library that loaded is not loaded again at every poll; one that did not is tried again (it may be
    /// installed while the daemon runs). Returns a message for the user when one is missing, otherwise null.
    /// </summary>
    internal static string? CheckAvailable(params string[] sonames)
    {
        var missing = Missing(sonames, soname => s_loaded.ContainsKey(soname) || (NativeLibrary.TryLoad(soname, out _) && s_loaded.TryAdd(soname, true)));
        return missing is null ? null : Describe(missing);
    }

    internal static string Describe(string soname) => soname switch
    {
        Xlib => $"{Xlib} (libX11, package libx11-6) is not installed, so Vigil cannot talk to an X server.",
        Xss => $"{Xss} (libXss, package libxss1) is not installed, so Vigil cannot read the idle time from the X server.",
        Xtst => $"{Xtst} (libXtst, package libxtst6) is not installed, so Vigil cannot post input through the XTest extension.",
        _ => $"{soname} is not installed.",
    };
}
