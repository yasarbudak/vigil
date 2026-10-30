using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;
using Vigil.Platform.MacOS;
using Vigil.Platform.Windows;

namespace Vigil.Platform;

/// <summary>The settings of every platform's section in config.json; only the running OS's section is used.</summary>
internal sealed record PlatformOptions(MacOSOptions MacOS, WindowsOptions Windows);

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
    public static PlatformServices Create(PlatformOptions options)
    {
        if (OperatingSystem.IsMacOS())
        {
            return CreateMacOS(options.MacOS);
        }

        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(options.Windows);
        }

        throw new PlatformNotSupportedException(
            $"Vigil does not support this platform yet ({RuntimeInformation.OSDescription}). Supported today: macOS and Windows.");
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

    /// <summary>Windows needs no permission to post input, so there is no requester.</summary>
    [SupportedOSPlatform("windows")]
    private static PlatformServices CreateWindows(WindowsOptions options) =>
        new(
            "Windows",
            new WindowsIdleSource(),
            new WindowsInputInjector(options),
            new WindowsSessionState(),
            new WindowsPermissions(),
            PermissionRequester: null);
}
