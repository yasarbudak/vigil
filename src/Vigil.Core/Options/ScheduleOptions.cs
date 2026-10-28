namespace Vigil.Core.Options;

/// <summary>
/// When Vigil is allowed to act. Default: always.
/// If <see cref="Start"/> equals <see cref="End"/> the window covers the whole day.
/// If <see cref="Start"/> is later than <see cref="End"/> the window crosses midnight and
/// belongs to the day it starts on (Friday 22:00-06:00 ends Saturday 06:00).
/// </summary>
public sealed class ScheduleOptions
{
    /// <summary>Days on which a window may start. <c>null</c> means every day.</summary>
    public DayOfWeek[]? ActiveDays { get; set; }

    public TimeOnly Start { get; set; } = TimeOnly.MinValue;

    public TimeOnly End { get; set; } = TimeOnly.MinValue;

    public bool IsActive(DateTime localNow)
    {
        var time = TimeOnly.FromDateTime(localNow);

        if (Start == End)
        {
            return IsDayActive(localNow.DayOfWeek);
        }

        if (Start < End)
        {
            return time >= Start && time < End && IsDayActive(localNow.DayOfWeek);
        }

        if (time >= Start)
        {
            return IsDayActive(localNow.DayOfWeek);
        }

        // After midnight: still inside the window that started yesterday.
        return time < End && IsDayActive(localNow.AddDays(-1).DayOfWeek);
    }

    public IEnumerable<string> Validate()
    {
        if (ActiveDays is { Length: 0 })
        {
            yield return "Schedule.ActiveDays must not be empty; omit it to be active every day.";
        }
        else if (ActiveDays is not null && ActiveDays.Any(day => !Enum.IsDefined(day)))
        {
            yield return "Schedule.ActiveDays contains an invalid day.";
        }
    }

    private bool IsDayActive(DayOfWeek day) => ActiveDays is null || Array.IndexOf(ActiveDays, day) >= 0;
}
