using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Windows;

/// <summary>
/// Windows asks no permission to post input with SendInput, so there is nothing to probe or request: the probe always
/// says granted and the platform has no <see cref="IPermissionRequester"/>. What can still stop an event is UIPI: it
/// only lets a process inject into windows of equal or lower integrity, so the event does not reach an elevated
/// foreground window (SendInput remarks). That shows up as a failed post or a failed verification, not as a missing permission.
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPermissions : IPermissionProbe
{
    public bool CanPostEvents() => true;
}
