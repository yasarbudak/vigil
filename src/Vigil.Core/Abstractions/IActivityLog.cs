namespace Vigil.Core.Abstractions;

public enum ActivityKind
{
    Injected,
    Degraded,
    Recovered,
    Paused,
    Resumed,
}

/// <param name="Timestamp">Local time of the event; its date decides which daily record it belongs to.</param>
/// <param name="Covered">For <see cref="ActivityKind.Injected"/>: user-absent time kept alive by this injection.</param>
public sealed record ActivityEntry(DateTimeOffset Timestamp, ActivityKind Kind, TimeSpan? Covered = null, string? Detail = null);

public readonly record struct DailyTotals(int Injections, TimeSpan Covered);

/// <summary>Persistent daily record of what Vigil did. Implementations own all I/O.</summary>
public interface IActivityLog
{
    void Record(ActivityEntry entry);

    DailyTotals ReadTotals(DateOnly day);

    /// <summary>Deletes the daily records that fall outside the retention period, counted back from <paramref name="today"/>.</summary>
    void Prune(DateOnly today);
}
