using Vigil.Configuration;
using Vigil.Platform;

namespace Vigil.Commands;

internal static class PermissionsCommand
{
    /// <summary>The only code path that may make the OS show a permission prompt.</summary>
    /// <param name="platformFactory">Replaces the OS implementations; used by tests so no real permission prompt can appear.</param>
    public static int Request(VigilPaths paths, TextWriter output, TextWriter error, Func<PlatformOptions, PlatformServices>? platformFactory = null)
    {
        try
        {
            var platform = (platformFactory ?? PlatformServices.Create)(VigilConfiguration.Load(paths.ConfigPath).Platform with { DataDirectory = paths.DataDirectory });
            if (platform.PermissionRequester is null)
            {
                // A platform without a requester either needs no permission (Windows) or has none Vigil can ask for.
                if (platform.Permissions.CanPostEvents())
                {
                    output.WriteLine($"Nothing to request: {platform.Name} needs no permission to post input events.");
                    return 0;
                }

                error.WriteLine("vigil: this platform has no permission to request.");
                return 1;
            }

            var granted = platform.PermissionRequester.RequestAccess();
            output.WriteLine(granted
                ? "Permission to post input events is granted."
                : platform.PermissionNotGrantedHint?.Invoke() ?? "Permission not granted.");
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
