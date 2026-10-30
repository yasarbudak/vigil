namespace Vigil.Install;

/// <summary>
/// The data-directory and log-file handling that <c>vigil install</c> and <c>vigil uninstall --purge</c> share on
/// every platform. Unix permission bits are set where they exist; on Windows the access rules are those the data
/// directory inherits (see <see cref="PrivateDirectory"/>), and the Windows branches below say where that matters.
/// </summary>
internal static class InstallFiles
{
    private const UnixFileMode LogMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

    private const UnixFileMode DefinitionMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

    /// <summary>Unix permission bits exist only on Unix; on Windows the file is created with the directory's inherited access rules.</summary>
    internal static FileStreamOptions CreateOptions(FileMode mode, UnixFileMode unixMode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = unixMode;
        }

        return options;
    }

    internal static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>
    /// Creates the data directory (private to the user) and makes sure the log file exists before the daemon first
    /// appends to it: 0600 on Unix, tightened if it already exists. On Windows an existing log is left untouched: a
    /// running daemon holds it open for writing without sharing the write access, so opening it again would fail.
    /// </summary>
    public static void PrepareLog(VigilPaths paths)
    {
        paths.EnsureDataDirectory();

        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(paths.LogPath))
            {
                using (new FileStream(paths.LogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                }
            }

            return;
        }

        using (new FileStream(paths.LogPath, CreateOptions(FileMode.OpenOrCreate, LogMode)))
        {
        }

        SetMode(paths.LogPath, LogMode);
    }

    /// <summary>
    /// Writes a temporary file next to the target, sets 0644 and renames it over the target (rename(2) is atomic), for the
    /// files the service managers read: the LaunchAgent plist and the systemd unit. A reader sees the old or the new content,
    /// never half of it.
    /// </summary>
    internal static void WriteAtomically(string path, string content)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, CreateOptions(FileMode.CreateNew, DefinitionMode)))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            SetMode(temporary, DefinitionMode); // Independent of the umask.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // No-op after a successful move.
        }
    }

    /// <summary>Removes the data directory, but only when it is exactly the default Vigil directory.</summary>
    public static int Purge(VigilPaths paths, string defaultDataDirectory, TextWriter output, TextWriter error)
    {
        // Windows paths are not case-sensitive; the Unix ones are.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = Path.GetFullPath(paths.DataDirectory);
        var expected = Path.GetFullPath(defaultDataDirectory);
        if (!string.Equals(target, expected, comparison) || !IsDefaultDirectoryName(Path.GetFileName(expected), comparison))
        {
            error.WriteLine($"vigil: refusing to purge '{target}': it is not the default Vigil data directory '{expected}'. Remove it yourself if that is what you want.");
            return 1;
        }

        // A symbolic link on Unix; on Windows also a junction or any other reparse point.
        var directory = new DirectoryInfo(target);
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            error.WriteLine($"vigil: refusing to purge '{target}': it is a symbolic link.");
            return 1;
        }

        if (!directory.Exists)
        {
            output.WriteLine($"Nothing to purge: {target} does not exist.");
            return 0;
        }

        DeleteRecursively(directory);
        output.WriteLine($"Purged data directory: {target}");
        return 0;
    }

    /// <summary>The default directory is named <c>Vigil</c> on macOS and Windows and <c>vigil</c> on Linux (XDG style); nothing else is purged.</summary>
    private static bool IsDefaultDirectoryName(string name, StringComparison comparison) =>
        string.Equals(name, "Vigil", comparison) || string.Equals(name, VigilPaths.DefaultDirectoryNameOnLinux, comparison);

    /// <summary>
    /// Deletes the directory. On Windows the daemon that was just stopped may still be closing its files, and a virus
    /// scanner or the search indexer may hold one for a moment, so a failure is retried a few times first.
    /// </summary>
    private static void DeleteRecursively(DirectoryInfo directory)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
            }
        }
    }
}
