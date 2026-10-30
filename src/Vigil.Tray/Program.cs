using Avalonia;
using Avalonia.Controls;

namespace Vigil.Tray.Ui;

/// <summary>
/// The tray: an icon with a menu and a settings window, and nothing else. It is a client of the daemon: it talks to it over the
/// local IPC socket (status, pause, resume, reload) and reads and writes config.json. It never posts input, never reads the idle
/// time and needs no OS permission, and it never starts, installs or stops the daemon (autostart is only <c>vigil install</c>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (PlatformSections.ForCurrentOS() is not { } platformSection)
        {
            Console.Error.WriteLine("vigil-tray: this operating system is not supported yet.");
            return 1;
        }

        var paths = VigilPaths.ForCurrentUser();
        using var instance = TrayInstanceLock.TryAcquire(paths);
        if (instance is null)
        {
            Console.Error.WriteLine("vigil-tray: a Vigil tray is already running.");
            return 0;
        }

        // OnExplicitShutdown: closing the settings window must not end the tray; only the menu's "Quit tray" does.
        return AppBuilder.Configure(() => new TrayApplication(paths, platformSection))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}
