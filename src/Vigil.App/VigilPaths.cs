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

    /// <summary>The daemon's own log, size-capped with one backup (<c>vigil.log.1</c>); see <c>Logging/RotatingFileWriter</c>.</summary>
    public string LogPath => Path.Combine(DataDirectory, "vigil.log");

    public string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>Lock file that keeps a second tray icon from starting (<c>Tray/TrayInstanceLock</c>).</summary>
    public string TrayLockPath => Path.Combine(DataDirectory, "vigil-tray.lock");

    /// <summary>
    /// The saved consent of the Linux desktop portal (Wayland): its restore token, one line, 0600. Written only by
    /// <c>vigil permissions request</c> and by a daemon that started a session with it; see <c>PortalTokenStore</c>.
    /// </summary>
    public string PortalTokenPath => Path.Combine(DataDirectory, "portal-restore-token");

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

        return LinuxDefaultDirectory(home, Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    /// <summary>
    /// The default directory on Linux: <c>$XDG_DATA_HOME/vigil</c>, with <c>$HOME/.local/share</c> when the variable is unset or
    /// empty (XDG Base Directory Specification, https://specifications.freedesktop.org/basedir-spec/latest/). The specification
    /// also says that a relative path in these variables "should be considered invalid and ignored", so a relative value falls
    /// back to the default as well. The data base (not the state base, which suits logs and history) is the right one for the
    /// single directory Vigil keeps: it holds the user's own <c>config.json</c>, which the specification's state directory,
    /// "not important or portable enough to the user that it should be stored in $XDG_DATA_HOME", is not for, and one
    /// directory is what the socket, the single-instance lock, <c>VIGIL_DATA_DIR</c> and <c>uninstall --purge</c> are built on.
    /// </summary>
    internal static string LinuxDefaultDirectory(string home, string? xdgDataHome)
    {
        var dataHome = !string.IsNullOrWhiteSpace(xdgDataHome) && xdgDataHome.StartsWith('/')
            ? xdgDataHome
            : Path.Combine(home, ".local", "share");
        return Path.Combine(dataHome, DefaultDirectoryNameOnLinux);
    }

    /// <summary>The leaf name of the default directory on Linux (lower case, as XDG directories are); macOS and Windows use <c>Vigil</c>.</summary>
    internal const string DefaultDirectoryNameOnLinux = "vigil";
}
