using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionState : ISessionState
{
    /// <summary>
    /// Asks the Remote Desktop Services API for the extended information of the session this process runs in
    /// (WTSSessionInfoEx) and reads its SessionFlags; see <see cref="SessionFlagsClassifier"/>. Null when the call
    /// fails or returns something other than the level 1 structure.
    /// </summary>
    public unsafe bool? IsScreenLocked()
    {
        if (!NativeMethods.WTSQuerySessionInformationW(
                Win32Constants.WtsCurrentServerHandle,
                Win32Constants.WtsCurrentSession,
                Win32Constants.WtsSessionInfoEx,
                out var buffer,
                out var bytesReturned))
        {
            return null;
        }

        try
        {
            if (buffer == nint.Zero || bytesReturned < WtsInfoExHead.Size)
            {
                return null;
            }

            var head = *(WtsInfoExHead*)buffer;
            return head.Level == 1 ? SessionFlagsClassifier.IsLocked(head.SessionFlags) : null;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public bool? IsOnBattery() =>
        NativeMethods.GetSystemPowerStatus(out var status) ? PowerStatusClassifier.IsOnBattery(status.AcLineStatus) : null;
}
