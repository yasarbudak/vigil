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

    public string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public string ActivityLogPath(DateOnly day) => Path.Combine(DataDirectory, $"activity-{day:yyyy-MM-dd}.jsonl");

    public static VigilPaths ForCurrentUser()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return new VigilPaths(string.IsNullOrWhiteSpace(overridden) ? DefaultDirectory() : overridden);
    }

    public void EnsureDataDirectory() => PrivateDirectory.Ensure(DataDirectory);

    private static string DefaultDirectory()
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
