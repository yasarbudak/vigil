using Vigil.Activity;
using Vigil.Core.Abstractions;

namespace Vigil.Tests;

public class ActivityLogRetentionTests
{
    private sealed class RecordingLog : IActivityLog
    {
        public List<DateOnly> Pruned { get; } = [];

        public void Record(ActivityEntry entry)
        {
        }

        public DailyTotals ReadTotals(DateOnly day) => default;

        public void Prune(DateOnly today) => Pruned.Add(today);
    }

    private sealed class ClockProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Runs_on_the_first_call()
    {
        var log = new RecordingLog();
        var retention = new ActivityLogRetention(log, new ClockProvider(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero)));

        retention.RunIfDue();

        Assert.Equal([new DateOnly(2026, 3, 9)], log.Pruned);
    }

    [Fact]
    public void Does_not_run_again_on_the_same_day()
    {
        var log = new RecordingLog();
        var clock = new ClockProvider(new DateTimeOffset(2026, 3, 9, 0, 0, 1, TimeSpan.Zero));
        var retention = new ActivityLogRetention(log, clock);

        retention.RunIfDue();
        clock.Now = new DateTimeOffset(2026, 3, 9, 23, 59, 59, TimeSpan.Zero);
        retention.RunIfDue();
        retention.RunIfDue();

        Assert.Single(log.Pruned);
    }

    [Fact]
    public void Runs_again_after_midnight()
    {
        var log = new RecordingLog();
        var clock = new ClockProvider(new DateTimeOffset(2026, 3, 9, 23, 59, 59, TimeSpan.Zero));
        var retention = new ActivityLogRetention(log, clock);

        retention.RunIfDue();
        clock.Now = clock.Now.AddSeconds(2);
        retention.RunIfDue();

        Assert.Equal([new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 10)], log.Pruned);
    }
}
