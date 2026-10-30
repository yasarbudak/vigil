using Vigil.Platform.Windows;

namespace Vigil.Tests;

public class WindowsClassifierTests
{
    [Theory]
    [InlineData(0u, true)] // WTS_SESSIONSTATE_LOCK
    [InlineData(1u, false)] // WTS_SESSIONSTATE_UNLOCK
    [InlineData(0xFFFFFFFFu, null)] // WTS_SESSIONSTATE_UNKNOWN
    [InlineData(2u, null)] // not a documented value
    [InlineData(0x80000000u, null)]
    public void Session_flags_are_classified_and_anything_else_is_unknown(uint flags, bool? expected)
    {
        Assert.Equal(expected, SessionFlagsClassifier.IsLocked(flags));
    }

    [Fact]
    public void Session_flag_values_match_the_documentation()
    {
        Assert.Equal(0u, SessionFlagsClassifier.Lock);
        Assert.Equal(1u, SessionFlagsClassifier.Unlock);
        Assert.Equal(4294967295u, SessionFlagsClassifier.Unknown);
    }

    [Theory]
    [InlineData((byte)0, true)] // Offline: running on battery
    [InlineData((byte)1, false)] // Online: on AC
    [InlineData((byte)255, null)] // Unknown status
    [InlineData((byte)2, null)] // not a documented value
    public void AC_line_status_is_classified_and_unknown_is_null(byte acLineStatus, bool? expected)
    {
        Assert.Equal(expected, PowerStatusClassifier.IsOnBattery(acLineStatus));
    }
}
