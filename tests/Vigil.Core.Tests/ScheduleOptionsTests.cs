using Vigil.Core.Options;

namespace Vigil.Core.Tests;

public class ScheduleOptionsTests
{
    // 2026-03-09 is a Monday.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0);

    [Fact]
    public void Default_schedule_is_always_active()
    {
        var schedule = new ScheduleOptions();

        Assert.True(schedule.IsActive(At(9, 0)));
        Assert.True(schedule.IsActive(At(14, 23, 59)));
    }

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    public void Same_day_window_includes_start_and_excludes_end(int hour, int minute, bool expected)
    {
        var schedule = new ScheduleOptions { Start = new TimeOnly(9, 0), End = new TimeOnly(17, 0) };

        Assert.Equal(expected, schedule.IsActive(At(9, hour, minute)));
    }

    [Theory]
    [InlineData(9, true)]   // Monday
    [InlineData(10, false)] // Tuesday
    [InlineData(11, true)]  // Wednesday
    [InlineData(14, false)] // Saturday
    public void Only_listed_days_are_active(int day, bool expected)
    {
        var schedule = new ScheduleOptions { ActiveDays = [DayOfWeek.Monday, DayOfWeek.Wednesday] };

        Assert.Equal(expected, schedule.IsActive(At(day, 12)));
    }

    [Theory]
    [InlineData(13, 21, 59, false)] // Friday before the window
    [InlineData(13, 22, 0, true)]   // Friday evening, window start
    [InlineData(14, 5, 59, true)]   // Saturday early morning, still Friday's window
    [InlineData(14, 6, 0, false)]   // Window end
    [InlineData(14, 22, 0, false)]  // Saturday evening, Saturday not active
    public void Overnight_window_belongs_to_the_day_it_starts_on(int day, int hour, int minute, bool expected)
    {
        var schedule = new ScheduleOptions
        {
            ActiveDays = [DayOfWeek.Friday],
            Start = new TimeOnly(22, 0),
            End = new TimeOnly(6, 0),
        };

        Assert.Equal(expected, schedule.IsActive(At(day, hour, minute)));
    }

    [Fact]
    public void Overnight_window_early_morning_is_inactive_when_the_previous_day_is_not_listed()
    {
        var schedule = new ScheduleOptions
        {
            ActiveDays = [DayOfWeek.Friday],
            Start = new TimeOnly(22, 0),
            End = new TimeOnly(6, 0),
        };

        Assert.False(schedule.IsActive(At(13, 5))); // Friday 05:00 belongs to Thursday's window.
    }

    [Fact]
    public void Empty_day_list_is_invalid()
    {
        Assert.NotEmpty(new ScheduleOptions { ActiveDays = [] }.Validate());
    }
}
