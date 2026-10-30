using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Vigil.Install;
using Vigil.Platform.MacOS.LaunchAgent;
using Vigil.Platform.Windows.Autostart;

namespace Vigil.Commands;

/// <summary>
/// <c>vigil install</c> and <c>vigil uninstall</c>: the only code that creates or removes an autostart entry,
/// and only when the user runs one of them. macOS uses a LaunchAgent, Windows a value in the per-user Run key.
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

            return Unsupported(error);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Fail(error, ex);
        }
    }

    private static int Unsupported(TextWriter error)
    {
        error.WriteLine($"vigil: install and uninstall are not supported yet on this platform ({RuntimeInformation.OSDescription}). Supported today: macOS and Windows.");
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

    [SupportedOSPlatform("windows")]
    private static RunKeyManager CreateWindowsManager(VigilPaths paths)
    {
        var daemon = new IpcDaemonStopper(paths.SocketPath); // One IPC probe for both stopping and "is it running".
        return new RunKeyManager(new RunKey(), daemon, daemon, new ProcessDaemonLauncher(), paths, VigilPaths.DefaultDirectory());
    }
}
