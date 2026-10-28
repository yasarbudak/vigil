using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Activity;

/// <summary>
/// One JSON line per event in <c>activity-yyyy-MM-dd.jsonl</c>. Entries hold counters and timestamps only,
/// never any content. A write failure is logged and does not stop the daemon. <see cref="Prune"/> deletes the
/// log's own daily files that are older than <see cref="ActivityLogOptions.RetentionDays"/>.
/// </summary>
internal sealed class JsonlActivityLog(VigilPaths paths, ActivityLogOptions options, ILogger<JsonlActivityLog> logger) : IActivityLog
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

    public void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-options.RetentionDays);
        var deleted = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(paths.DataDirectory, VigilPaths.ActivityLogSearchPattern))
            {
                if (TryDeleteExpired(path, oldest))
                {
                    deleted++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list {Directory} to remove old activity logs.", paths.DataDirectory);
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} activity log file(s) older than {Days} days.", deleted, options.RetentionDays);
        }
    }

    /// <summary>
    /// Deletes one file if it is a regular file that carries one of this log's names and a day before
    /// <paramref name="oldest"/>. Symbolic links and directories are never touched.
    /// </summary>
    private bool TryDeleteExpired(string path, DateOnly oldest)
    {
        if (!VigilPaths.TryParseActivityLogDay(Path.GetFileName(path), out var day) || day >= oldest)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory))
            {
                return false;
            }

            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the old activity log {Path}.", path);
            return false;
        }
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
