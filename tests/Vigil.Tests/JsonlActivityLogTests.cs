using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Activity;
using Vigil.Core.Abstractions;

namespace Vigil.Tests;

public class JsonlActivityLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

    private static JsonlActivityLog Create(TempDataDirectory dir) => new(dir.Paths, NullLogger<JsonlActivityLog>.Instance);

    [Fact]
    public void Totals_sum_only_Injected_entries_of_the_requested_day()
    {
        using var dir = new TempDataDirectory();
        var log = Create(dir);
        log.Record(new ActivityEntry(Noon, ActivityKind.Injected, TimeSpan.FromMinutes(4)));
        log.Record(new ActivityEntry(Noon.AddMinutes(4), ActivityKind.Injected, TimeSpan.FromMinutes(4)));
        log.Record(new ActivityEntry(Noon.AddMinutes(5), ActivityKind.Degraded, Detail: "x"));
        log.Record(new ActivityEntry(Noon.AddDays(1), ActivityKind.Injected, TimeSpan.FromMinutes(9)));

        var totals = log.ReadTotals(new DateOnly(2026, 3, 9));

        Assert.Equal(2, totals.Injections);
        Assert.Equal(TimeSpan.FromMinutes(8), totals.Covered);
    }

    [Fact]
    public void Missing_file_means_empty_totals()
    {
        using var dir = new TempDataDirectory();

        Assert.Equal(default, Create(dir).ReadTotals(new DateOnly(2026, 3, 9)));
    }

    [Fact]
    public void A_damaged_line_is_skipped_without_losing_the_rest()
    {
        using var dir = new TempDataDirectory();
        var log = Create(dir);
        log.Record(new ActivityEntry(Noon, ActivityKind.Injected, TimeSpan.FromMinutes(4)));
        File.AppendAllText(dir.Paths.ActivityLogPath(new DateOnly(2026, 3, 9)), "{not json\n");
        log.Record(new ActivityEntry(Noon.AddMinutes(4), ActivityKind.Injected, TimeSpan.FromMinutes(4)));

        Assert.Equal(2, log.ReadTotals(new DateOnly(2026, 3, 9)).Injections);
    }

    [Fact]
    public void Entries_hold_counters_and_timestamps_only()
    {
        using var dir = new TempDataDirectory();
        Create(dir).Record(new ActivityEntry(Noon, ActivityKind.Injected, TimeSpan.FromMinutes(4)));

        var line = File.ReadAllLines(dir.Paths.ActivityLogPath(new DateOnly(2026, 3, 9))).Single();

        Assert.Equal("{\"timestamp\":\"2026-03-09T12:00:00+00:00\",\"kind\":\"Injected\",\"covered\":\"00:04:00\"}", line);
    }

    [Fact]
    public void Log_file_is_readable_by_the_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDataDirectory();
        Create(dir).Record(new ActivityEntry(Noon, ActivityKind.Injected, TimeSpan.Zero));

        var mode = File.GetUnixFileMode(dir.Paths.ActivityLogPath(new DateOnly(2026, 3, 9)));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
