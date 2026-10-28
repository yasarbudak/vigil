using Vigil.Configuration;
using Vigil.Platform;

namespace Vigil.Commands;

internal static class PermissionsCommand
{
    /// <summary>The only code path that may make the OS show a permission prompt.</summary>
    public static int Request(VigilPaths paths, TextWriter output, TextWriter error)
    {
        try
        {
            var platform = PlatformServices.Create(VigilConfiguration.Load(paths.ConfigPath).MacOS);
            if (platform.PermissionRequester is null)
            {
                error.WriteLine("vigil: this platform has no permission to request.");
                return 1;
            }

            var granted = platform.PermissionRequester.RequestAccess();
            output.WriteLine(granted
                ? "Permission to post input events is granted."
                : "Permission not granted yet. Allow 'vigil' (or your terminal) in System Settings > Privacy & Security, then run 'vigil diag'.");
            return granted ? 0 : 1;
        }
        catch (PlatformNotSupportedException ex)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 1;
        }
        catch (ConfigurationException ex)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 2;
        }
    }
}
