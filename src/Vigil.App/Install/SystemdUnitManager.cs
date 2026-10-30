using Vigil.Platform.Linux.Autostart;

namespace Vigil.Install;

/// <summary>
/// Explicit, user-requested autostart on Linux: a systemd user unit that starts <c>vigil run</c> with the graphical session.
/// Nothing here runs unless the user invokes <c>vigil install</c> or <c>vigil uninstall</c>. systemctl is injected so tests
/// never call it. The rules mirror <see cref="LaunchAgentManager"/>: a unit that differs from what would be written is
/// replaced only with <c>--force</c>, installing again with the same settings changes nothing (except that a unit that is not
/// enabled and running is brought up), and <c>--purge</c> deletes only the default data directory. Like
/// <c>launchctl bootstrap</c>, <c>enable --now</c> starts the daemon right away. Without a systemd user instance install
/// refuses and says so, and writes nothing. The daemon's output goes to the journal, so no <c>vigil.log</c> is prepared.
/// </summary>
internal sealed class SystemdUnitManager(
    ISystemctl systemctl,
    string unitDirectory,
    VigilPaths paths,
    string defaultDataDirectory)
{
    public const string UnitName = SystemdUserUnit.UnitName;

    public string UnitPath => Path.Combine(unitDirectory, UnitName);

    public int Install(string? executablePath, bool force, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrEmpty(executablePath) || !executablePath.StartsWith('/') || !File.Exists(executablePath))
        {
            error.WriteLine($"vigil: refusing to install: cannot tell where the vigil binary is ('{executablePath}').");
            return 1;
        }

        var manager = systemctl.CheckUserManager();
        if (!manager.Succeeded)
        {
            error.WriteLine("vigil: refusing to install: there is no systemd user instance to talk to, and Vigil's autostart on Linux is a systemd user unit.");
            error.WriteLine($"systemctl --user said: {(string.IsNullOrWhiteSpace(manager.Output) ? "no output" : manager.Output)}");
            error.WriteLine("Start Vigil from your session's own autostart with 'vigil run' instead, or run 'vigil install' from a login session that has systemd --user.");
            return 1;
        }

        string desired;
        try
        {
            desired = SystemdUserUnit.Render(new SystemdUnitSpec(
                executablePath,
                Path.GetFullPath(paths.DataDirectory) == Path.GetFullPath(defaultDataDirectory) ? null : Path.GetFullPath(paths.DataDirectory)));
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"vigil: refusing to install: {ex.Message}");
            return 1;
        }

        // Compared as bytes: the file is either exactly what this version would write, or it is "different".
        var exists = File.Exists(UnitPath);
        var identical = exists && File.ReadAllBytes(UnitPath).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(desired));
        if (exists && !identical && !force)
        {
            error.WriteLine($"vigil: a different Vigil unit already exists at {UnitPath} (other binary location, other data directory, or not written by this version).");
            error.WriteLine("Run 'vigil uninstall' first, or 'vigil install --force' to replace it.");
            return 1;
        }

        var enabled = systemctl.IsEnabled(UnitName);
        var active = systemctl.IsActive(UnitName);
        if (identical && enabled && active)
        {
            output.WriteLine($"Already installed and running: {UnitPath}");
            return 0;
        }

        paths.EnsureDataDirectory();

        if (!identical)
        {
            // Stop before replacing, so the daemon that runs afterwards is the one the new unit describes.
            if ((enabled || active) && !Disable(error))
            {
                return 1;
            }

            Directory.CreateDirectory(unitDirectory);
            InstallFiles.WriteAtomically(UnitPath, desired);
        }

        var reload = systemctl.DaemonReload();
        if (!reload.Succeeded)
        {
            error.WriteLine($"vigil: systemctl --user daemon-reload failed (exit {reload.ExitCode}): {reload.Output}");
            error.WriteLine($"The unit is at {UnitPath}; remove it with 'vigil uninstall'.");
            return 1;
        }

        var start = systemctl.EnableNow(UnitName);
        if (!start.Succeeded)
        {
            error.WriteLine($"vigil: systemctl --user enable --now failed (exit {start.ExitCode}): {start.Output}");
            error.WriteLine($"The unit is at {UnitPath}. See 'systemctl --user status {UnitName}' and 'journalctl --user -u {UnitName}'; remove it with 'vigil uninstall'.");
            return 1;
        }

        output.WriteLine($"Installed: {UnitPath}");
        output.WriteLine("Vigil now starts with your graphical session and restarts after a crash. Log: journalctl --user -u vigil");
        output.WriteLine("Remove it with 'vigil uninstall'.");
        return 0;
    }

    public int Uninstall(bool purge, TextWriter output, TextWriter error)
    {
        var removed = false;
        var present = File.Exists(UnitPath);

        if (present || systemctl.IsEnabled(UnitName) || systemctl.IsActive(UnitName))
        {
            // With the unit file gone the manager may still know the unit; a failure then is not the user's problem.
            if (!Disable(error) && present)
            {
                return 1;
            }

            removed = true;
        }

        if (present)
        {
            File.Delete(UnitPath);
            var reload = systemctl.DaemonReload();
            if (!reload.Succeeded)
            {
                error.WriteLine($"vigil: the unit file was removed, but systemctl --user daemon-reload failed (exit {reload.ExitCode}): {reload.Output}");
            }
        }

        output.WriteLine(removed ? $"Uninstalled: {UnitPath}" : "Not installed, nothing to remove.");

        return purge ? InstallFiles.Purge(paths, defaultDataDirectory, output, error) : 0;
    }

    private bool Disable(TextWriter error)
    {
        var result = systemctl.DisableNow(UnitName);
        if (result.Succeeded)
        {
            return true;
        }

        error.WriteLine($"vigil: systemctl --user disable --now failed (exit {result.ExitCode}): {result.Output}");
        return false;
    }
}
