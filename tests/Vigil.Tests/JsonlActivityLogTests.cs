using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Activity;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Tests;

public class JsonlActivityLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 3, 9);

    private static readonly string[] KeptNames =
    [
        "activity-2026-03-09.jsonl", "activity-2026-03-02.jsonl", "activity-2026-03-08.jsonl",
        "activity-2026-03-10.jsonl", "activity-2027-01-01.jsonl",
    ];

    private static JsonlActivityLog Create(TempDataDirectory dir, int retentionDays = 30) =>
        new(dir.Paths, new ActivityLogOptions { RetentionDays = retentionDays }, NullLogger<JsonlActivityLog>.Instance);

    private static string Touch(TempDataDirectory dir, string name)
    {
        var path = Path.Combine(dir.Paths.DataDirectory, name);
        File.WriteAllText(path, "x");
        return path;
    }

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

    [Fact]
    public void Prune_deletes_daily_files_older_than_the_retention_period()
    {
        using var dir = new TempDataDirectory();
        var old = Touch(dir, "activity-2026-02-07.jsonl"); // 30 days before today is 2026-02-07: kept
        var older = Touch(dir, "activity-2026-02-06.jsonl");
        var ancient = Touch(dir, "activity-2020-01-01.jsonl");

        Create(dir, retentionDays: 30).Prune(Today);

        Assert.True(File.Exists(old));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(ancient));
    }

    [Fact]
    public void Prune_keeps_files_within_the_retention_period_including_today_and_future_days()
    {
        using var dir = new TempDataDirectory();
        var kept = new List<string>();
        foreach (var name in KeptNames)
        {
            kept.Add(Touch(dir, name));
        }

        Create(dir, retentionDays: 7).Prune(Today);

        Assert.All(kept, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void Retention_of_one_day_keeps_today_and_yesterday_only()
    {
        using var dir = new TempDataDirectory();
        var yesterday = Touch(dir, "activity-2026-03-08.jsonl");
        var before = Touch(dir, "activity-2026-03-07.jsonl");

        Create(dir, retentionDays: 1).Prune(Today);

        Assert.True(File.Exists(yesterday));
        Assert.False(File.Exists(before));
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("notes.txt")]
    [InlineData("activity-2020-01-01.txt")]
    [InlineData("activity-2020-01-01.jsonl.bak")]
    [InlineData("old-activity-2020-01-01.jsonl")]
    [InlineData("activity-2020-1-1.jsonl")]
    [InlineData("activity-2020-13-45.jsonl")]
    [InlineData("activity-.jsonl")]
    [InlineData("activity-abc.jsonl")]
    [InlineData("ACTIVITY-2020-01-01.jsonl")]
    public void Prune_never_touches_files_that_do_not_match_the_log_file_name(string name)
    {
        using var dir = new TempDataDirectory();
        var path = Touch(dir, name);

        Create(dir, retentionDays: 1).Prune(Today);

        Assert.True(File.Exists(path));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void Prune_does_not_enter_subdirectories_or_remove_a_directory_with_a_log_name()
    {
        using var dir = new TempDataDirectory();
        var folder = Path.Combine(dir.Paths.DataDirectory, "activity-2020-01-01.jsonl");
        Directory.CreateDirectory(folder);
        var inner = Path.Combine(folder, "activity-2019-01-01.jsonl");
        File.WriteAllText(inner, "x");

        Create(dir, retentionDays: 1).Prune(Today);

        Assert.True(File.Exists(inner));
    }

    [Fact]
    public void Prune_never_follows_or_removes_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDataDirectory();
        using var outside = new TempDataDirectory();
        var target = Touch(outside, "precious.txt");
        var targetFolder = Path.Combine(outside.Paths.DataDirectory, "folder");
        Directory.CreateDirectory(targetFolder);
        File.WriteAllText(Path.Combine(targetFolder, "keep.txt"), "x");

        var fileLink = Path.Combine(dir.Paths.DataDirectory, "activity-2020-01-01.jsonl");
        var folderLink = Path.Combine(dir.Paths.DataDirectory, "activity-2020-01-02.jsonl");
        var brokenLink = Path.Combine(dir.Paths.DataDirectory, "activity-2020-01-03.jsonl");
        File.CreateSymbolicLink(fileLink, target);
        Directory.CreateSymbolicLink(folderLink, targetFolder);
        File.CreateSymbolicLink(brokenLink, Path.Combine(outside.Paths.DataDirectory, "missing"));

        Create(dir, retentionDays: 1).Prune(Today);

        Assert.NotNull(new FileInfo(fileLink).LinkTarget);
        Assert.NotNull(new FileInfo(folderLink).LinkTarget);
        Assert.NotNull(new FileInfo(brokenLink).LinkTarget);
        Assert.Equal("x", File.ReadAllText(target));
        Assert.True(File.Exists(Path.Combine(targetFolder, "keep.txt")));
    }

    [Fact]
    public void Prune_with_a_missing_data_directory_does_not_throw()
    {
        var log = new JsonlActivityLog(
            new VigilPaths(Path.Combine(Path.GetTempPath(), "vigil-missing-" + Guid.NewGuid().ToString("N"))),
            new ActivityLogOptions(),
            NullLogger<JsonlActivityLog>.Instance);

        log.Prune(Today);
    }

    [Fact]
    public void Day_in_a_file_name_round_trips_through_the_path()
    {
        using var dir = new TempDataDirectory();

        var parsed = VigilPaths.TryParseActivityLogDay(Path.GetFileName(dir.Paths.ActivityLogPath(Today)), out var day);

        Assert.True(parsed);
        Assert.Equal(Today, day);
    }
}
