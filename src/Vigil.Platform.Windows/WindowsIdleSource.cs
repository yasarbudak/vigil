using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Windows;

/// <summary>
/// Reads the time of the last input event of this session (GetLastInputInfo). This is the only thing Vigil reads about
/// user activity. Input injected with SendInput counts as input, which is what the verification step relies on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIdleSource : IIdleSource
{
    /// <exception cref="Win32Exception">GetLastInputInfo failed. The worker logs it and tries again at the next poll.</exception>
    public TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Unsafe.SizeOf<LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetLastInputInfo failed.");
        }

        // Read after GetLastInputInfo, so the current tick count is never older than the input tick count.
        return IdleTimeCalculator.Elapsed(NativeMethods.GetTickCount(), info.Time);
    }
}
