using Vigil.Daemon;
using Vigil.Platform.Windows.Autostart;

namespace Vigil.Install;

/// <summary>
/// Explicit, user-requested autostart on Windows: a value in the per-user Run key that starts <c>vigild.exe</c> at
/// sign-in. Nothing here runs unless the user invokes <c>vigil install</c> or <c>vigil uninstall</c>. The registry and
/// the running daemon are injected so tests never touch either. The rules mirror <see cref="LaunchAgentManager"/>:
/// an entry that differs from what would be written is replaced only with <c>--force</c>, installing again with the
/// same settings changes nothing, and <c>--purge</c> deletes only the default data directory.
/// </summary>
internal sealed class RunKeyManager(
    IRunKey runKey,
    IDaemonStopper daemon,
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
        try
        {
            command = DaemonArguments.Build(
                daemonPath,
                string.Equals(Path.GetFullPath(paths.DataDirectory), Path.GetFullPath(defaultDataDirectory), StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Path.GetFullPath(paths.DataDirectory));
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

        if (identical)
        {
            output.WriteLine($"Already installed: {Location}");
            return 0;
        }

        InstallFiles.PrepareLog(paths);
        runKey.Write(ValueName, command);

        output.WriteLine($"Installed: {Location}");
        output.WriteLine($"Vigil now starts at your next sign-in. Start it now by running {daemonPath}. Log: {paths.LogPath}");
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
