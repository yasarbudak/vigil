using System.Runtime.Versioning;
using System.Text;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSSessionState : ISessionState
{
    /// <summary>
    /// Polls the window server session dictionary (CGSessionCopyCurrentDictionary). Null when it cannot be read or
    /// understood; see <see cref="SessionLockClassifier"/> for the rules and for the private key it relies on.
    /// </summary>
    public bool? IsScreenLocked()
    {
        // Copy rule: released by the handle. NULL (no GUI session, window server disabled) means unknown.
        using var session = NativeMethods.CGSessionCopyCurrentDictionary();
        if (session.IsInvalid)
        {
            return null;
        }

        return SessionLockClassifier.IsScreenLocked(
            Read(session, SessionLockClassifier.OnConsoleKey),
            Read(session, SessionLockClassifier.LoginDoneKey),
            Read(session, SessionLockClassifier.ScreenIsLockedKey));
    }

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

    /// <summary>
    /// Looks one key up. The key string is created and released per call (Create rule): three tiny allocations per
    /// poll are negligible and keep the class free of static native state, which a cached key would need.
    /// </summary>
    private static SessionKeyValue Read(CFTypeHandle dictionary, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key + "\0");
        using var cfKey = CreateString(bytes);
        if (cfKey.IsInvalid)
        {
            return SessionKeyValue.NotBoolean;
        }

        if (!NativeMethods.CFDictionaryGetValueIfPresent(dictionary, cfKey, out var value))
        {
            return SessionKeyValue.Absent;
        }

        // "Get" rule: the value belongs to the dictionary and must not be released. A present key with a NULL or
        // non-CFBoolean value is reported as not boolean.
        if (value == nint.Zero || NativeMethods.CFGetTypeID(value) != NativeMethods.CFBooleanGetTypeID())
        {
            return SessionKeyValue.NotBoolean;
        }

        return NativeMethods.CFBooleanGetValue(value) ? SessionKeyValue.True : SessionKeyValue.False;
    }

    private static unsafe CFTypeHandle CreateString(byte[] nulTerminatedUtf8)
    {
        fixed (byte* pointer = nulTerminatedUtf8)
        {
            return NativeMethods.CFStringCreateWithCString(nint.Zero, pointer, NativeMethods.StringEncodingUtf8);
        }
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
