using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Install;
using Vigil.Platform.MacOS.LaunchAgent;

namespace Vigil.Commands;

/// <summary>
/// <c>vigil install</c> and <c>vigil uninstall</c>: the only code that creates or removes an autostart entry,
/// and only when the user runs one of them.
/// </summary>
internal static class AutostartCommand
{
    public static int Install(VigilPaths paths, bool force, TextWriter output, TextWriter error) =>
        Execute(error, manager => manager.Install(Environment.ProcessPath, force, output, error), paths);

    public static int Uninstall(VigilPaths paths, bool purge, TextWriter output, TextWriter error) =>
        Execute(error, manager => manager.Uninstall(purge, output, error), paths);

    private static int Execute(TextWriter error, Func<LaunchAgentManager, int> action, VigilPaths paths)
    {
        if (!OperatingSystem.IsMacOS())
        {
            error.WriteLine($"vigil: install and uninstall are not supported yet on this platform ({RuntimeInformation.OSDescription}). Supported today: macOS.");
            return 1;
        }

        try
        {
            return action(CreateManager(paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("macos")]
    private static LaunchAgentManager CreateManager(VigilPaths paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LaunchAgentManager(
            Launchctl.ForCurrentUser(),
            Path.Combine(home, "Library", "LaunchAgents"),
            paths,
            VigilPaths.DefaultDirectory());
    }
}
