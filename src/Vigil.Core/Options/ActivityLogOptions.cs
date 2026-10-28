namespace Vigil.Core.Options;

public sealed class ActivityLogOptions
{
    public const int MinRetentionDays = 1;

    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// Persist daily counters (number of injections, time covered) to the per-user data directory.
    /// When false nothing is written; counters then live in memory only and restart at zero.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Daily files whose day is more than this many days before today are deleted (at daemon start and at
    /// each day rollover). With 30, today and the 30 days before it are kept.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    public IEnumerable<string> Validate()
    {
        if (RetentionDays is < MinRetentionDays or > MaxRetentionDays)
        {
            yield return $"ActivityLog.RetentionDays must be between {MinRetentionDays} and {MaxRetentionDays}.";
        }
    }
}
