using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.MacOS;

/// <summary>Reads the HID idle counter. This is the only thing Vigil reads about user activity.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSIdleSource : IIdleSource
{
    public TimeSpan GetIdleTime()
    {
        var seconds = NativeMethods.CGEventSourceSecondsSinceLastEventType(
            NativeMethods.EventSourceStateHidSystemState,
            NativeMethods.AnyInputEventType);

        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }
}
