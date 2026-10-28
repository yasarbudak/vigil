using System.Runtime.Versioning;
using System.Text;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSSessionState : ISessionState
{
    /// <summary>
    /// Always unknown. The lock flag lives under the key "CGSSessionScreenIsLocked", which is not declared in
    /// the public CGSession.h of the SDK, so it is deliberately not used.
    /// </summary>
    public bool? IsScreenLocked() => null;

    public bool? IsOnBattery()
    {
        using var snapshot = NativeMethods.IOPSCopyPowerSourcesInfo();
        if (snapshot.IsInvalid)
        {
            return null;
        }

        // "Get" rule: the returned string belongs to the snapshot and must not be released.
        var sourceType = NativeMethods.IOPSGetProvidingPowerSourceType(snapshot);
        return sourceType == nint.Zero ? null : PowerSourceClassifier.IsOnBattery(ReadString(sourceType));
    }

    private static unsafe string? ReadString(nint cfString)
    {
        Span<byte> buffer = stackalloc byte[64];
        fixed (byte* pointer = buffer)
        {
            if (!NativeMethods.CFStringGetCString(cfString, pointer, buffer.Length, NativeMethods.StringEncodingUtf8))
            {
                return null;
            }
        }

        var length = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(buffer[..(length < 0 ? buffer.Length : length)]);
    }
}
