using Vigil.Daemon;
using Vigil.Platform.Windows.Autostart;

namespace Vigil.Install;

/// <summary>
/// Explicit, user-requested autostart on Windows: a value in the per-user Run key that starts <c>vigild.exe</c> at
/// sign-in. Nothing here runs unless the user invokes <c>vigil install</c> or <c>vigil uninstall</c>. The registry and
/// the running daemon are injected so tests never touch either. The rules mirror <see cref="LaunchAgentManager"/>:
/// an entry that differs from what would be written is replaced only with <c>--force</c>, installing again with the
/// same settings changes nothing (except that a daemon that is not running is started), and <c>--purge</c> deletes only
/// the default data directory. Like <c>launchctl bootstrap</c> on macOS, install starts the daemon right away.
/// </summary>
internal sealed class RunKeyManager(
    IRunKey runKey,
    IDaemonStopper daemon,
    IDaemonProbe probe,
    IDaemonLauncher launcher,
    VigilPaths paths,
    string defaultDataDirectory)
{
    /// <summary>Name of the value under the Run key; the one entry Vigil ever writes.</summary>
    public const string ValueName = "Vigil";

    /// <summary>File name of the daemon, which sits next to <c>vigil.exe</c> in the zip (scripts/package-windows.ps1).</summary>
    public const string DaemonFileName = "vigild.exe";

    private static string Location => $@"HKEY_CURRENT_USER\{RunKeyLocation.Path}\{ValueName}";

    public int Install(string? executablePath, bool force, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrEmpty(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            error.WriteLine($"vigil: refusing to install: cannot tell where vigil.exe is ('{executablePath}').");
            return 1;
        }

        var daemonPath = Path.Combine(Path.GetDirectoryName(executablePath)!, DaemonFileName);
        if (!File.Exists(daemonPath))
        {
            error.WriteLine($"vigil: refusing to install: {DaemonFileName} was not found next to vigil.exe ({daemonPath}).");
            error.WriteLine($"Autostart runs {DaemonFileName}, the windowless daemon. Use the Vigil folder as it comes in the zip, with both programs in it.");
            return 1;
        }

        string command;
        string arguments;
        try
        {
            var dataDirectory = string.Equals(Path.GetFullPath(paths.DataDirectory), Path.GetFullPath(defaultDataDirectory), StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.GetFullPath(paths.DataDirectory);
            command = DaemonArguments.Build(daemonPath, dataDirectory);
            arguments = DaemonArguments.BuildArguments(dataDirectory);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"vigil: refusing to install: {ex.Message}");
            return 1;
        }

        // Compared as text: the value is either exactly what this version would write, or it is "different".
        var existing = runKey.Read(ValueName);
        var identical = existing is { Text: { } text } && string.Equals(text, command, StringComparison.Ordinal);
        if (existing is not null && !identical && !force)
        {
            error.WriteLine($"vigil: a different Vigil autostart entry already exists at {Location} (other location, other data directory, or not written by this version).");
            error.WriteLine("Run 'vigil uninstall' first, or 'vigil install --force' to replace it.");
            return 1;
        }

        var running = probe.IsRunning();
        var wasRunning = running;
        if (identical && running)
        {
            output.WriteLine($"Already installed and running: {Location}");
            return 0;
        }

        InstallFiles.PrepareLog(paths);

        if (!identical)
        {
            // Replacing an entry (--force): stop the daemon before the value changes, as macOS unloads before replacing the
            // plist, so the daemon that runs afterwards is the one the new value describes. If it cannot be stopped the old
            // value stays.
            if (existing is not null && running)
            {
                var stopped = daemon.Stop();
                if (stopped.Outcome == DaemonStopOutcome.Failed)
                {
                    error.WriteLine($"vigil: could not stop the running Vigil daemon: {stopped.Detail}");
                    error.WriteLine("The autostart entry was left as it was.");
                    return 1;
                }

                running = false;
            }

            runKey.Write(ValueName, command);
        }

        // As on macOS, an entry that is already right but whose daemon is not running only gets the start. On a fresh
        // install a daemon that is already running (for example `vigil run`) is left alone.
        if (!running)
        {
            var started = launcher.Start(daemonPath, arguments);
            if (!started.Succeeded)
            {
                error.WriteLine($"vigil: could not start {DaemonFileName} ({daemonPath}): {started.Detail}");
                error.WriteLine($"The autostart entry is at {Location}. Start the daemon by running {DaemonFileName}; remove the entry with 'vigil uninstall'.");
                return 1;
            }
        }

        output.WriteLine($"Installed: {Location}");
        if (wasRunning && !running)
        {
            output.WriteLine("Stopped the running Vigil daemon to replace it.");
        }

        output.WriteLine(running
            ? $"Vigil now starts at your next sign-in. A daemon was already running and was left alone. Log: {paths.LogPath}"
            : $"Vigil is running now and starts at your next sign-in. Log: {paths.LogPath}");
        output.WriteLine("Remove it with 'vigil uninstall'.");
        return 0;
    }

    public int Uninstall(bool purge, TextWriter output, TextWriter error)
    {
        // Stop first, as the macOS flow unloads first: if the daemon cannot be stopped the entry stays, so the user can try again.
        var stopped = daemon.Stop();
        if (stopped.Outcome == DaemonStopOutcome.Failed)
        {
            error.WriteLine($"vigil: could not stop the running Vigil daemon: {stopped.Detail}");
            error.WriteLine("The autostart entry was left in place.");
            return 1;
        }

        var removed = stopped.Outcome == DaemonStopOutcome.Stopped;
        if (removed)
        {
            output.WriteLine("Stopped the running Vigil daemon.");
        }

        if (runKey.Delete(ValueName))
        {
            removed = true;
        }

        output.WriteLine(removed ? $"Uninstalled: {Location}" : "Not installed, nothing to remove.");

        return purge ? InstallFiles.Purge(paths, defaultDataDirectory, output, error) : 0;
    }
}
