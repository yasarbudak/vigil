using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.MacOS;

/// <summary>
/// Vigil needs only the permission to post events. It never asks for Input Monitoring.
/// <see cref="CanPostEvents"/> is silent; <see cref="RequestAccess"/> may show the system prompt.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSPermissions : IPermissionProbe, IPermissionRequester
{
    public bool CanPostEvents() => NativeMethods.CGPreflightPostEventAccess();

    public bool RequestAccess() => NativeMethods.CGRequestPostEventAccess();
}
