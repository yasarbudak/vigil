using Vigil.Platform.MacOS.LaunchAgent;

namespace Vigil.Install;

/// <summary>
/// Explicit, user-requested autostart (macOS LaunchAgent). Nothing here runs unless the user invokes
/// <c>vigil install</c> or <c>vigil uninstall</c>. launchctl is injected so tests never call it.
/// </summary>
internal sealed class LaunchAgentManager(
    ILaunchctl launchctl,
    string launchAgentsDirectory,
    VigilPaths paths,
    string defaultDataDirectory)
{
    /// <summary>Same as the bundle identifier in the app's Info.plist (scripts/package-macos.sh).</summary>
    public const string Label = "io.github.yasarbudak.vigil";

    private const UnixFileMode PlistMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

    private const UnixFileMode LogMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

    public string PlistPath => Path.Combine(launchAgentsDirectory, Label + ".plist");

    public int Install(string? executablePath, bool force, TextWriter output, TextWriter error)
    {
        if (!AppBundle.TryGetBundlePath(executablePath, out var bundlePath))
        {
            error.WriteLine($"vigil: refusing to install: '{executablePath}' is not inside an app bundle (.../Vigil.app/Contents/MacOS/vigil).");
            error.WriteLine("macOS ties the permission to post input events to the app bundle, so an autostart entry pointing at a loose binary would never get it.");
            error.WriteLine("Build the bundle with scripts/package-macos.sh and run 'vigil install' from inside it.");
            return 1;
        }

        if (!File.Exists(AppBundle.InfoPlistPath(bundlePath)))
        {
            error.WriteLine($"vigil: refusing to install: '{bundlePath}' has no Contents/Info.plist, so it is not a usable app bundle.");
            return 1;
        }

        var spec = new LaunchAgentSpec(
            Label,
            executablePath!,
            Label,
            Path.GetFullPath(paths.DataDirectory) == Path.GetFullPath(defaultDataDirectory) ? null : Path.GetFullPath(paths.DataDirectory));
        var desired = LaunchAgentPlist.Render(spec);

        // Compared as bytes: the file is either exactly what this version would write, or it is "different".
        var exists = File.Exists(PlistPath);
        var identical = exists && File.ReadAllBytes(PlistPath).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(desired));
        if (exists && !identical && !force)
        {
            error.WriteLine($"vigil: a different Vigil LaunchAgent already exists at {PlistPath} (other app location, other data directory, or not written by this version).");
            error.WriteLine("Run 'vigil uninstall' first, or 'vigil install --force' to replace it.");
            return 1;
        }

        var loaded = launchctl.IsLoaded(Label);
        if (identical && loaded)
        {
            output.WriteLine($"Already installed and loaded: {PlistPath}");
            return 0;
        }

        PrepareLog();

        if (!identical)
        {
            // Unload before replacing, so launchd never holds a definition that differs from the file.
            if (loaded && !Unload(error))
            {
                return 1;
            }

            Directory.CreateDirectory(launchAgentsDirectory);
            WriteAtomically(PlistPath, desired);
            loaded = false;
        }

        var result = launchctl.Bootstrap(PlistPath);
        if (!result.Succeeded)
        {
            error.WriteLine($"vigil: launchctl bootstrap failed (exit {result.ExitCode}): {result.Output}");
            error.WriteLine($"The plist is at {PlistPath}. Decode the code with 'launchctl error {result.ExitCode}'; remove the plist with 'vigil uninstall'.");
            return 1;
        }

        output.WriteLine($"Installed: {PlistPath}");
        output.WriteLine($"Vigil now starts at login and restarts after a crash. Log: {paths.LogPath}");
        output.WriteLine("Remove it with 'vigil uninstall'.");
        return 0;
    }

    public int Uninstall(bool purge, TextWriter output, TextWriter error)
    {
        var removed = false;

        if (launchctl.IsLoaded(Label))
        {
            if (!Unload(error))
            {
                return 1;
            }

            removed = true;
        }

        if (File.Exists(PlistPath))
        {
            File.Delete(PlistPath);
            removed = true;
        }

        output.WriteLine(removed ? $"Uninstalled: {PlistPath}" : "Not installed, nothing to remove.");

        return purge ? Purge(output, error) : 0;
    }

    private bool Unload(TextWriter error)
    {
        var result = launchctl.Bootout(Label);
        if (result.Succeeded)
        {
            return true;
        }

        error.WriteLine($"vigil: launchctl bootout failed (exit {result.ExitCode}): {result.Output}");
        return false;
    }

    /// <summary>Removes the data directory, but only when it is exactly the default Vigil directory.</summary>
    private int Purge(TextWriter output, TextWriter error)
    {
        var target = Path.GetFullPath(paths.DataDirectory);
        var expected = Path.GetFullPath(defaultDataDirectory);
        if (target != expected || Path.GetFileName(expected) != "Vigil")
        {
            error.WriteLine($"vigil: refusing to purge '{target}': it is not the default Vigil data directory '{expected}'. Remove it yourself if that is what you want.");
            return 1;
        }

        var directory = new DirectoryInfo(target);
        if (directory.LinkTarget is not null)
        {
            error.WriteLine($"vigil: refusing to purge '{target}': it is a symbolic link.");
            return 1;
        }

        if (!directory.Exists)
        {
            output.WriteLine($"Nothing to purge: {target} does not exist.");
            return 0;
        }

        directory.Delete(recursive: true);
        output.WriteLine($"Purged data directory: {target}");
        return 0;
    }

    /// <summary>Creates the data directory (0700) and makes sure the log file exists and is 0600 before the daemon first appends to it.</summary>
    private void PrepareLog()
    {
        paths.EnsureDataDirectory();
        using (new FileStream(paths.LogPath, CreateOptions(FileMode.OpenOrCreate, LogMode)))
        {
        }

        SetMode(paths.LogPath, LogMode);
    }

    /// <summary>Unix permission bits exist only on Unix; the Windows branch is never used, install is macOS only for now.</summary>
    private static FileStreamOptions CreateOptions(FileMode mode, UnixFileMode unixMode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = unixMode;
        }

        return options;
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>Writes a temporary file next to the target, sets 0644 and renames it over the target (rename(2) is atomic).</summary>
    internal static void WriteAtomically(string path, string content)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, CreateOptions(FileMode.CreateNew, PlistMode)))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            SetMode(temporary, PlistMode); // Independent of the umask.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // No-op after a successful move.
        }
    }
}
