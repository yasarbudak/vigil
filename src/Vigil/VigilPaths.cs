using System.Globalization;

namespace Vigil;

/// <summary>
/// The single place that knows where Vigil keeps files. Everything (config, socket, activity log)
/// lives in one per-user directory that only the current user can open.
/// </summary>
internal sealed class VigilPaths(string dataDirectory)
{
    /// <summary>Overrides the data directory, e.g. for trying out a config without touching the real one.</summary>
    internal const string DataDirectoryVariable = "VIGIL_DATA_DIR";

    public string DataDirectory { get; } = dataDirectory;

    public string SocketPath => Path.Combine(DataDirectory, "vigil.sock");

    /// <summary>Receives stdout and stderr of the daemon when launchd runs it (see <c>vigil install</c>).</summary>
    public string LogPath => Path.Combine(DataDirectory, "vigil.log");

    public string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>Search pattern that selects the daily activity log files in <see cref="DataDirectory"/>.</summary>
    public const string ActivityLogSearchPattern = "activity-*.jsonl";

    private const string ActivityLogPrefix = "activity-";
    private const string ActivityLogSuffix = ".jsonl";
    private const string ActivityLogDayFormat = "yyyy-MM-dd";

    public string ActivityLogPath(DateOnly day) => Path.Combine(DataDirectory, $"{ActivityLogPrefix}{day.ToString(ActivityLogDayFormat, CultureInfo.InvariantCulture)}{ActivityLogSuffix}");

    /// <summary>
    /// Reads the day out of a file name written by <see cref="ActivityLogPath"/>. False for any other name,
    /// so callers can tell Vigil's own files from everything else in the directory.
    /// </summary>
    public static bool TryParseActivityLogDay(string fileName, out DateOnly day)
    {
        day = default;
        return fileName.StartsWith(ActivityLogPrefix, StringComparison.Ordinal)
            && fileName.EndsWith(ActivityLogSuffix, StringComparison.Ordinal)
            && fileName.Length > ActivityLogPrefix.Length + ActivityLogSuffix.Length
            && DateOnly.TryParseExact(
                fileName.AsSpan(ActivityLogPrefix.Length, fileName.Length - ActivityLogPrefix.Length - ActivityLogSuffix.Length),
                ActivityLogDayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out day);
    }

    public static VigilPaths ForCurrentUser()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return new VigilPaths(string.IsNullOrWhiteSpace(overridden) ? DefaultDirectory() : overridden);
    }

    public void EnsureDataDirectory() => PrivateDirectory.Ensure(DataDirectory);

    /// <summary>The data directory used when <c>VIGIL_DATA_DIR</c> is not set.</summary>
    public static string DefaultDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "Vigil");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil");
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "vigil");
    }
}
