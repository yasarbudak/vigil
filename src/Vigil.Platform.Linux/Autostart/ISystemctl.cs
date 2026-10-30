namespace Vigil.Platform.Linux.Autostart;

public readonly record struct SystemctlResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The few systemctl operations Vigil needs, for the current user's manager (<c>--user</c>). Behind an interface so tests never call systemctl.</summary>
public interface ISystemctl
{
    /// <summary>
    /// Whether a systemd user manager can be talked to (<c>systemctl --user show-environment</c> succeeds). The command's
    /// output is the manager's environment and is never kept or shown; only the error text of a failure is returned.
    /// </summary>
    SystemctlResult CheckUserManager();

    SystemctlResult DaemonReload();

    /// <summary><c>enable --now</c>: links the unit into its target's wants and starts it.</summary>
    SystemctlResult EnableNow(string unit);

    /// <summary><c>disable --now</c>: removes the links and stops the unit.</summary>
    SystemctlResult DisableNow(string unit);

    /// <summary>Exit code 0 of <c>is-enabled</c>: "at least one is enabled".</summary>
    bool IsEnabled(string unit);

    /// <summary>Exit code 0 of <c>is-active</c>: "at least one is active (i.e. running)".</summary>
    bool IsActive(string unit);
}
