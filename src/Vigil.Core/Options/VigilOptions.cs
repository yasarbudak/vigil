namespace Vigil.Core.Options;

public sealed class VigilOptions
{
    /// <summary>Idle time at which Vigil acts. Teams goes Away after about 5 minutes, so the default leaves a margin.</summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>How often idle time is read.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Wait between injecting and re-reading idle time to verify the reset.</summary>
    public TimeSpan VerifyDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Minimum spacing between attempts while Degraded, so a failing injection is not repeated every poll.</summary>
    public TimeSpan DegradedRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool PauseOnLock { get; set; } = true;

    public bool PauseOnBattery { get; set; }

    public ScheduleOptions Schedule { get; set; } = new();

    public ActivityLogOptions ActivityLog { get; set; } = new();

    /// <summary>Returns every violated rule; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Threshold <= TimeSpan.Zero)
        {
            errors.Add("Threshold must be greater than zero.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            errors.Add("PollInterval must be greater than zero.");
        }
        else if (PollInterval >= Threshold)
        {
            errors.Add("PollInterval must be shorter than Threshold.");
        }

        if (VerifyDelay < TimeSpan.Zero)
        {
            errors.Add("VerifyDelay must not be negative.");
        }
        else if (PollInterval > TimeSpan.Zero && VerifyDelay >= PollInterval)
        {
            errors.Add("VerifyDelay must be shorter than PollInterval.");
        }

        if (DegradedRetryInterval < PollInterval)
        {
            errors.Add("DegradedRetryInterval must not be shorter than PollInterval.");
        }

        if (Schedule is null)
        {
            errors.Add("Schedule must not be null.");
        }
        else
        {
            errors.AddRange(Schedule.Validate());
        }

        if (ActivityLog is null)
        {
            errors.Add("ActivityLog must not be null.");
        }
        else
        {
            errors.AddRange(ActivityLog.Validate());
        }

        return errors;
    }
}
