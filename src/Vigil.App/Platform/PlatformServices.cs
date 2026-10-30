using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;
using Vigil.Platform.Linux;
using Vigil.Platform.MacOS;
using Vigil.Platform.Windows;

namespace Vigil.Platform;

/// <summary>The settings of every platform's section in config.json; only the running OS's section is used.</summary>
/// <param name="DataDirectory">
/// The data directory in use, for the platform that keeps a file there (the Linux portal consent). Null means the
/// default directory of the current user.
/// </param>
internal sealed record PlatformOptions(MacOSOptions MacOS, WindowsOptions Windows, LinuxOptions Linux, string? DataDirectory = null);

/// <summary>One fact about the platform for <c>vigil diag</c>.</summary>
internal readonly record struct PlatformDetail(string Label, string Value);

/// <summary>The OS-specific implementations of the Core abstractions.</summary>
/// <param name="Summary">One line about how this platform works here, shown by <c>vigil status</c>; null when there is nothing to add.</param>
/// <param name="StartupProblem">When set, the daemon cannot work in this process and refuses to start with this message.</param>
/// <param name="PermissionNotGrantedHint">The next step for the user after a permission request that was not granted.</param>
/// <param name="Details">Extra read-only lines for <c>vigil diag</c>.</param>
internal sealed record PlatformServices(
    string Name,
    IIdleSource Idle,
    IInputInjector Injector,
    ISessionState Session,
    IPermissionProbe Permissions,
    IPermissionRequester? PermissionRequester,
    string? Summary = null,
    string? StartupProblem = null,
    Func<string>? PermissionNotGrantedHint = null,
    Func<IReadOnlyList<PlatformDetail>>? Details = null)
{
    /// <summary>What a macOS user does after a permission request that was not granted.</summary>
    internal const string MacOSNotGrantedHint =
        "Permission not granted yet. Allow 'vigil' (or your terminal) in System Settings > Privacy & Security, then run 'vigil diag'.";

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

        if (OperatingSystem.IsLinux())
        {
            return CreateLinux(options.Linux, options.DataDirectory);
        }

        throw new PlatformNotSupportedException(
            $"Vigil does not support this platform yet ({RuntimeInformation.OSDescription}). Supported today: macOS, Windows and Linux.");
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
            permissions,
            PermissionNotGrantedHint: () => MacOSNotGrantedHint);
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

    [SupportedOSPlatform("linux")]
    private static PlatformServices CreateLinux(LinuxOptions options, string? dataDirectory)
    {
        var paths = dataDirectory is null ? VigilPaths.ForCurrentUser() : new VigilPaths(dataDirectory);
        var linux = LinuxPlatform.Create(options, paths.PortalTokenPath);
        return new PlatformServices(
            "Linux",
            linux.Idle,
            linux.Injector,
            linux.Session,
            linux.Permissions,
            linux.PermissionRequester,
            linux.Summary,
            linux.StartupProblem,
            linux.NotGrantedHint,
            () => [.. linux.Diagnose().Select(d => new PlatformDetail(d.Label, d.Value))]);
    }
}
