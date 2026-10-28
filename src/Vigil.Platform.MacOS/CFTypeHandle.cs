using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigil.Platform.MacOS;

/// <summary>Owns a CoreFoundation object obtained from a "Create"/"Copy" function and releases it exactly once.</summary>
[SupportedOSPlatform("macos")]
internal sealed class CFTypeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CFTypeHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.CFRelease(handle);
        return true;
    }
}
