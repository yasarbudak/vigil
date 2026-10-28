using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vigil.Core.Abstractions;

namespace Vigil.Activity;

/// <summary>
/// One JSON line per event in <c>activity-yyyy-MM-dd.jsonl</c>. Entries hold counters and timestamps only,
/// never any content. A write failure is logged and does not stop the daemon.
/// </summary>
internal sealed class JsonlActivityLog(VigilPaths paths, ILogger<JsonlActivityLog> logger) : IActivityLog
{
    private readonly object _gate = new();

    public void Record(ActivityEntry entry)
    {
        var path = paths.ActivityLogPath(DateOnly.FromDateTime(entry.Timestamp.DateTime));
        var line = JsonSerializer.SerializeToUtf8Bytes(entry, VigilJsonContext.Default.ActivityEntry);

        lock (_gate)
        {
            try
            {
                using var stream = Open(path);
                stream.Write(line);
                stream.WriteByte((byte)'\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not write the activity log {Path}.", path);
            }
        }
    }

    public DailyTotals ReadTotals(DateOnly day)
    {
        var path = paths.ActivityLogPath(day);
        if (!File.Exists(path))
        {
            return default;
        }

        var injections = 0;
        var covered = TimeSpan.Zero;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (TryParse(line) is { Kind: ActivityKind.Injected } entry)
                {
                    injections++;
                    covered += entry.Covered ?? TimeSpan.Zero;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the activity log {Path}.", path);
        }

        return new DailyTotals(injections, covered);
    }

    private static ActivityEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, VigilJsonContext.Default.ActivityEntry);
        }
        catch (JsonException)
        {
            return null; // A damaged line must not hide the rest of the day.
        }
    }

    private static FileStream Open(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
