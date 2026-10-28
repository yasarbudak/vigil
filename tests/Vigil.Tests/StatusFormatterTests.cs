using Vigil.Commands;
using Vigil.Core.Presence;

namespace Vigil.Tests;

public class StatusFormatterTests
{
    private static string Format(bool pauseOnLock, bool? lockStateKnown)
    {
        var at = new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero);
        var status = new PresenceStatus(
            PresenceState.Watching, null, null, null, null, null, 0, TimeSpan.Zero, at, pauseOnLock, lockStateKnown);
        using var writer = new StringWriter();
        StatusFormatter.Write(writer, status);
        return writer.ToString();
    }

    [Fact]
    public void Unknown_lock_state_with_PauseOnLock_is_shown_prominently()
    {
        var text = Format(pauseOnLock: true, lockStateKnown: false);

        Assert.Contains("Screen lock:     UNKNOWN", text, StringComparison.Ordinal);
        Assert.Contains("NOT pausing while locked", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_lock_state_with_PauseOnLock_is_reported_as_detected()
    {
        Assert.Contains("Screen lock:     detected", Format(pauseOnLock: true, lockStateKnown: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_state_is_not_alarming_when_PauseOnLock_is_off()
    {
        var text = Format(pauseOnLock: false, lockStateKnown: false);

        Assert.Contains("not used", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNKNOWN", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_state_before_the_first_poll_is_not_checked_yet()
    {
        Assert.Contains("not checked yet", Format(pauseOnLock: true, lockStateKnown: null), StringComparison.Ordinal);
    }
}
