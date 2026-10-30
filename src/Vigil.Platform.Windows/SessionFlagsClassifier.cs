namespace Vigil.Platform.Windows;

/// <summary>Interprets WTSINFOEX_LEVEL1_W.SessionFlags. Pure: no Win32 calls.</summary>
internal static class SessionFlagsClassifier
{
    // WTS_SESSIONSTATE_LOCK = 0, WTS_SESSIONSTATE_UNLOCK = 1, WTS_SESSIONSTATE_UNKNOWN = 0xFFFFFFFF
    // https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w
    internal const uint Lock = 0;
    internal const uint Unlock = 1;
    internal const uint Unknown = 0xFFFFFFFF;

    /// <summary>
    /// Vigil supports Windows 10 and later. On Windows 7 and Windows Server 2008 R2 the documentation says the two flags
    /// are reversed "due to a code defect"; that is deliberately not compensated for, because no supported system has it.
    /// </summary>
    /// <returns>True when locked, false when unlocked, null for WTS_SESSIONSTATE_UNKNOWN and for any value not documented.</returns>
    public static bool? IsLocked(uint sessionFlags) => sessionFlags switch
    {
        Lock => true,
        Unlock => false,
        _ => null,
    };
}
