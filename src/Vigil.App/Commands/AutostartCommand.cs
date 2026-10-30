using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Vigil.Install;
using Vigil.Platform.Linux.Autostart;
using Vigil.Platform.MacOS.LaunchAgent;
using Vigil.Platform.Windows.Autostart;

namespace Vigil.Commands;

/// <summary>
/// <c>vigil install</c> and <c>vigil uninstall</c>: the only code that creates or removes an autostart entry,
/// and only when the user runs one of them. macOS uses a LaunchAgent, Windows a value in the per-user Run key, Linux a
/// systemd user unit.
/// </summary>
internal static class AutostartCommand
{
    public static int Install(VigilPaths paths, bool force, TextWriter output, TextWriter error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateWindowsManager(paths).Install(Environment.ProcessPath, force, output, error);
            }

            if (OperatingSystem.IsMacOS())
            {
                return CreateMacOSManager(paths).Install(Environment.ProcessPath, force, output, error);
            }

            if (OperatingSystem.IsLinux())
            {
                return CreateLinuxManager(paths).Install(Environment.ProcessPath, force, output, error);
            }

            return Unsupported(error);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Fail(error, ex);
        }
    }

    public static int Uninstall(VigilPaths paths, bool purge, TextWriter output, TextWriter error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateWindowsManager(paths).Uninstall(purge, output, error);
            }

            if (OperatingSystem.IsMacOS())
            {
                return CreateMacOSManager(paths).Uninstall(purge, output, error);
            }

            if (OperatingSystem.IsLinux())
            {
                return CreateLinuxManager(paths).Uninstall(purge, output, error);
            }

            return Unsupported(error);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Fail(error, ex);
        }
    }

    private static int Unsupported(TextWriter error)
    {
        error.WriteLine($"vigil: install and uninstall are not supported yet on this platform ({RuntimeInformation.OSDescription}). Supported today: macOS, Windows and Linux.");
        return 1;
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or SecurityException;

    private static int Fail(TextWriter error, Exception ex)
    {
        error.WriteLine($"vigil: {ex.Message}");
        return 1;
    }

    [SupportedOSPlatform("macos")]
    private static LaunchAgentManager CreateMacOSManager(VigilPaths paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LaunchAgentManager(
            Launchctl.ForCurrentUser(),
            Path.Combine(home, "Library", "LaunchAgents"),
            paths,
            VigilPaths.DefaultDirectory());
    }

    /// <summary>
    /// The user unit directory is <c>$XDG_CONFIG_HOME/systemd/user</c>, or <c>~/.config/systemd/user</c> when the variable is
    /// unset or not absolute (systemd.unit(5), "Unit File Load Path"; the XDG specification ignores relative values).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static SystemdUnitManager CreateLinuxManager(VigilPaths paths)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !configHome.StartsWith('/'))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return new SystemdUnitManager(
            new Systemctl(),
            Path.Combine(configHome, "systemd", "user"),
            paths,
            VigilPaths.DefaultDirectory());
    }

    [SupportedOSPlatform("windows")]
    private static RunKeyManager CreateWindowsManager(VigilPaths paths)
    {
        var daemon = new IpcDaemonStopper(paths.SocketPath); // One IPC probe for both stopping and "is it running".
        return new RunKeyManager(new RunKey(), daemon, daemon, new ProcessDaemonLauncher(), paths, VigilPaths.DefaultDirectory());
    }
}
