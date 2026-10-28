using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;
using Vigil.Platform.MacOS;

namespace Vigil.Platform;

/// <summary>The OS-specific implementations of the Core abstractions.</summary>
internal sealed record PlatformServices(
    string Name,
    IIdleSource Idle,
    IInputInjector Injector,
    ISessionState Session,
    IPermissionProbe Permissions,
    IPermissionRequester? PermissionRequester)
{
    /// <summary>The one place where the running OS is mapped to its implementations. A new platform adds a branch here.</summary>
    public static PlatformServices Create(MacOSOptions macOS)
    {
        if (OperatingSystem.IsMacOS())
        {
            return CreateMacOS(macOS);
        }

        throw new PlatformNotSupportedException(
            $"Vigil does not support this platform yet ({RuntimeInformation.OSDescription}). Supported today: macOS.");
    }

    [SupportedOSPlatform("macos")]
    private static PlatformServices CreateMacOS(MacOSOptions options)
    {
        var permissions = new MacOSPermissions();
        var session = new MacOSSessionState();
        return new PlatformServices(
            "macOS",
            new MacOSIdleSource(),
            new MacOSInputInjector(options),
            session,
            permissions,
            permissions);
    }
}
