using Vigil.Platform.MacOS;
using static Vigil.Platform.MacOS.SessionKeyValue;

namespace Vigil.Tests;

public class SessionLockClassifierTests
{
    [Theory]
    // Neither public key present: not a session dictionary we understand, whatever the lock key says.
    [InlineData(Absent, Absent, Absent, null)]
    [InlineData(Absent, Absent, False, null)]
    [InlineData(Absent, Absent, True, null)]
    [InlineData(Absent, Absent, NotBoolean, null)]
    // Normal console session.
    [InlineData(True, True, Absent, false)]
    [InlineData(True, True, False, false)]
    [InlineData(True, True, True, true)]
    [InlineData(True, True, NotBoolean, null)]
    // Either public key alone is enough to trust the dictionary.
    [InlineData(True, Absent, Absent, false)]
    [InlineData(Absent, True, Absent, false)]
    [InlineData(Absent, True, True, true)]
    [InlineData(Absent, False, Absent, false)]
    [InlineData(Absent, NotBoolean, Absent, false)]
    // Session switched away (fast user switching): treated as locked, whatever the lock key says.
    [InlineData(False, True, Absent, true)]
    [InlineData(False, True, False, true)]
    [InlineData(False, Absent, Absent, true)]
    [InlineData(False, True, NotBoolean, true)]
    // OnConsole present but not a boolean: the format changed, so unknown.
    [InlineData(NotBoolean, True, False, null)]
    [InlineData(NotBoolean, Absent, True, null)]
    public void Session_values_are_classified(SessionKeyValue onConsole, SessionKeyValue loginDone, SessionKeyValue screenIsLocked, bool? expected)
    {
        Assert.Equal(expected, SessionLockClassifier.IsScreenLocked(onConsole, loginDone, screenIsLocked));
    }

    [Fact]
    public void Public_key_strings_match_CGSession_h()
    {
        Assert.Equal("kCGSSessionOnConsoleKey", SessionLockClassifier.OnConsoleKey);
        Assert.Equal("kCGSessionLoginDoneKey", SessionLockClassifier.LoginDoneKey);
        Assert.Equal("CGSSessionScreenIsLocked", SessionLockClassifier.ScreenIsLockedKey);
    }
}
