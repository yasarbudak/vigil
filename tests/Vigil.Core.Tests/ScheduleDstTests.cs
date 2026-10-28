using Vigil.Core.Presence;

namespace Vigil.Core.Tests;

/// <summary>
/// Schedules are wall-clock windows. These tests pin down what that means on the two DST transitions of
/// America/New_York in 2026: 8 March 02:00 jumps to 03:00 (gap), 1 November 02:00 falls back to 01:00 (fold).
/// </summary>
public class ScheduleDstTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static PresenceRig RigAt(DateTimeOffset utc, TimeOnly start, TimeOnly end) =>
        new(o =>
            {
                o.Schedule.Start = start;
                o.Schedule.End = end;
            },
            start: utc,
            zone: NewYork);

    private static PauseReason? PauseReasonAt(PresenceRig rig)
    {
        rig.Engine.Evaluate(new PresenceObservation(TimeSpan.Zero, false, false, true));
        return rig.Status.PauseReason;
    }

    [Theory]
    [InlineData("2026-03-08T06:59:00Z", true)]  // 01:59 EST, before the gap
    [InlineData("2026-03-08T07:00:00Z", true)]  // 03:00 EDT, first wall-clock time after the gap
    [InlineData("2026-03-08T07:29:00Z", true)]  // 03:29 EDT
    [InlineData("2026-03-08T07:30:00Z", false)] // 03:30 EDT, window end
    public void Window_spanning_the_spring_forward_gap_follows_wall_clock_time(string utc, bool active)
    {
        var rig = RigAt(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(1, 0), new TimeOnly(3, 30));

        Assert.Equal(active ? null : PauseReason.OutsideSchedule, PauseReasonAt(rig));
    }

    [Theory]
    [InlineData("2026-03-08T06:59:00Z", false)] // 01:59 EST: window starts at 02:30, which does not exist today
    [InlineData("2026-03-08T07:00:00Z", true)]  // 03:00 EDT is already past 02:30 on the wall clock
    public void Window_starting_inside_the_gap_opens_at_the_first_real_time_after_it(string utc, bool active)
    {
        var rig = RigAt(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(2, 30), new TimeOnly(6, 0));

        Assert.Equal(active ? null : PauseReason.OutsideSchedule, PauseReasonAt(rig));
    }

    [Theory]
    [InlineData("2026-11-01T05:30:00Z", true)]  // 01:30 EDT, first occurrence
    [InlineData("2026-11-01T06:30:00Z", true)]  // 01:30 EST, second occurrence
    [InlineData("2026-11-01T05:00:00Z", false)] // 01:00 EDT, before the window
    [InlineData("2026-11-01T06:45:00Z", false)] // 01:45 EST, window end
    public void Both_occurrences_of_a_repeated_hour_are_treated_alike(string utc, bool active)
    {
        var rig = RigAt(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(1, 15), new TimeOnly(1, 45));

        Assert.Equal(active ? null : PauseReason.OutsideSchedule, PauseReasonAt(rig));
    }

    [Theory]
    [InlineData("2026-11-01T04:30:00Z", true)]  // 00:30 EDT, still inside Saturday night's window
    [InlineData("2026-11-01T07:30:00Z", true)]  // 02:30 EST, after the fold, still inside
    [InlineData("2026-11-01T11:00:00Z", false)] // 06:00 EST, window end
    public void Overnight_window_across_the_fall_back_fold_stays_continuous(string utc, bool active)
    {
        var rig = RigAt(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(22, 0), new TimeOnly(6, 0));

        Assert.Equal(active ? null : PauseReason.OutsideSchedule, PauseReasonAt(rig));
    }

    [Fact]
    public void PauseUntilTomorrow_with_a_start_inside_the_gap_ends_shortly_after_the_gap()
    {
        var rig = RigAt(new DateTimeOffset(2026, 3, 7, 15, 0, 0, TimeSpan.Zero), new TimeOnly(2, 30), new TimeOnly(6, 0));

        var until = rig.Engine.PauseUntilTomorrow();

        Assert.InRange(until, new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void PauseUntilTomorrow_is_a_whole_calendar_day_away_across_the_fall_back_day()
    {
        var rig = RigAt(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero), TimeOnly.MinValue, TimeOnly.MinValue);

        var until = rig.Engine.PauseUntilTomorrow();

        // Midnight local on 1 November is still EDT (UTC-4).
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), until);
    }
}
