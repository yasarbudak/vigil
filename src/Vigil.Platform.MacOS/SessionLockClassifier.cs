namespace Vigil.Platform.MacOS;

/// <summary>What the session dictionary holds for one key.</summary>
public enum SessionKeyValue
{
    /// <summary>The key is not in the dictionary.</summary>
    Absent,

    /// <summary>The key is present but its value is not a CFBoolean.</summary>
    NotBoolean,

    False,

    True,
}

/// <summary>Interprets the values read from the CGSessionCopyCurrentDictionary dictionary. Pure: no CoreFoundation.</summary>
internal static class SessionLockClassifier
{
    // CGSession.h: kCGSessionOnConsoleKey CFSTR("kCGSSessionOnConsoleKey"), a CFBoolean
    internal const string OnConsoleKey = "kCGSSessionOnConsoleKey";

    // CGSession.h: kCGSessionLoginDoneKey CFSTR("kCGSessionLoginDoneKey"), a CFBoolean (the spelling differs from the key above in the header)
    internal const string LoginDoneKey = "kCGSessionLoginDoneKey";

    // NOT in the public SDK headers. Defined in Apple's open-source xnu, iokit/IOKit/IOKitKeysPrivate.h, as
    // kIOConsoleSessionScreenIsLockedKey "CGSSessionScreenIsLocked", and read by configd for the same purpose.
    // It is a private contract: the unknown result below is what protects Vigil if Apple changes it.
    internal const string ScreenIsLockedKey = "CGSSessionScreenIsLocked";

    /// <returns>
    /// True when locked or the session is not on the console (fast user switching), false when unlocked,
    /// null when the dictionary does not look like a session dictionary or the lock flag is not a boolean.
    /// </returns>
    public static bool? IsScreenLocked(SessionKeyValue onConsole, SessionKeyValue loginDone, SessionKeyValue screenIsLocked)
    {
        // Guard against silent fail-open: without either public key the dictionary is not one we understand,
        // and a missing private key would otherwise read as "unlocked".
        if (onConsole == SessionKeyValue.Absent && loginDone == SessionKeyValue.Absent)
        {
            return null;
        }

        // A public key that is no longer a boolean means the format changed; do not guess.
        if (onConsole == SessionKeyValue.NotBoolean)
        {
            return null;
        }

        if (onConsole == SessionKeyValue.False)
        {
            return true;
        }

        return screenIsLocked switch
        {
            SessionKeyValue.True => true,
            SessionKeyValue.False => false,
            SessionKeyValue.Absent => false,
            _ => null,
        };
    }
}
