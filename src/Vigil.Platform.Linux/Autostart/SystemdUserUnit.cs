using System.Text;

namespace Vigil.Platform.Linux.Autostart;

/// <param name="ExecutablePath">Absolute path of the <c>vigil</c> binary the unit starts with <c>run</c>.</param>
/// <param name="DataDirectoryOverride">Set only when the user chose a non-default data directory; passed on to the daemon.</param>
public sealed record SystemdUnitSpec(string ExecutablePath, string? DataDirectoryOverride = null);

/// <summary>
/// Builds the per-user systemd unit. Pure: no file or systemctl access. Like the macOS plist it is write-only: an existing
/// file is compared byte for byte with what <see cref="Render"/> produces, so no unit-file parser is needed. Directives are
/// from systemd.unit(5), systemd.service(5), systemd.exec(5) and systemd.special(7)
/// (https://www.freedesktop.org/software/systemd/man/latest/).
/// </summary>
public static class SystemdUserUnit
{
    public const string UnitName = "vigil.service";

    /// <summary>Seconds between a crash and the next start; the same 30 s as the macOS LaunchAgent's ThrottleInterval.</summary>
    public const int RestartSeconds = 30;

    /// <summary>Name of the environment variable that moves the data directory (see <c>VigilPaths</c>).</summary>
    public const string DataDirectoryVariable = "VIGIL_DATA_DIR";

    /// <summary>
    /// Restart policy: <c>Restart=on-abnormal</c> restarts only after an unclean signal (a crash, a kill), a timeout or the
    /// watchdog, and not after any exit code. The table in systemd.service(5), "Exit causes and the effect of the Restart=
    /// settings", has an unclean exit code under <c>on-failure</c> and not under <c>on-abnormal</c>; the same page names
    /// <c>on-abnormal</c> as the alternative "for services that shall be able to terminate on their own choice (and avoid
    /// immediate restarting)". That is the macOS <c>KeepAlive { Crashed = true }</c> exactly: the deliberate exits (2 invalid
    /// configuration, 1 another instance running or a failed start) are not restarted, so there is no restart loop, and with
    /// <c>on-failure</c> they would be unless <c>RestartPreventExitStatus=1 2</c> listed each code. SIGTERM (what
    /// <c>systemctl stop</c> and a normal logout send) is a clean signal, and a stop through systemctl is never restarted.
    /// <para>
    /// The unit is part of the graphical session: <c>WantedBy=graphical-session.target</c> starts it with the session,
    /// <c>PartOf=</c> stops it when the session ends ("Such services should have PartOf=graphical-session.target"), and
    /// <c>After=</c> starts it once the session's environment (DISPLAY, WAYLAND_DISPLAY, ...) has been handed to the user
    /// manager (systemd.special(7), graphical-session.target).
    /// </para>
    /// </summary>
    public static string Render(SystemdUnitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!spec.ExecutablePath.StartsWith('/'))
        {
            throw new ArgumentException("The path must be absolute.", nameof(spec));
        }

        var unit = new StringBuilder();
        unit.Append("# Written by 'vigil install'. Remove it with 'vigil uninstall'.\n");
        unit.Append("[Unit]\n");
        unit.Append("Description=Vigil keeps chat apps from showing you as Away while you are at your desk\n");
        unit.Append("Documentation=https://github.com/yasarbudak/vigil\n");
        unit.Append("PartOf=graphical-session.target\n");
        unit.Append("After=graphical-session.target\n");
        unit.Append('\n');
        unit.Append("[Service]\n");
        unit.Append("Type=simple\n");
        unit.Append("ExecStart=").Append(QuoteExecStart(spec.ExecutablePath)).Append(" run\n");
        unit.Append("Restart=on-abnormal\n");
        unit.Append("RestartSec=").Append(RestartSeconds).Append('\n');
        if (spec.DataDirectoryOverride is { } dataDirectory)
        {
            if (!dataDirectory.StartsWith('/'))
            {
                throw new ArgumentException("The data directory must be an absolute path.", nameof(spec));
            }

            unit.Append("Environment=").Append(Quote($"{DataDirectoryVariable}={dataDirectory}", escapeDollar: false)).Append('\n');
        }

        unit.Append('\n');
        unit.Append("[Install]\n");
        unit.Append("WantedBy=graphical-session.target\n");
        return unit.ToString();
    }

    /// <summary>
    /// ExecStart: the command line is unquoted by the rules of systemd.syntax(7) (double quotes wrap a whole item, C-style
    /// escapes, backslash doubled) and then the specifiers and variables of systemd.service(5) are applied, where "%" starts a
    /// specifier (written "%%") and "$" a variable (written "$$").
    /// </summary>
    internal static string QuoteExecStart(string value) => Quote(value, escapeDollar: true);

    /// <summary>
    /// Environment= is unquoted the same way, and "Specifier expansion is performed" but "the '$' character has no special
    /// meaning" (systemd.exec(5)), so only "%" is doubled.
    /// </summary>
    internal static string Quote(string value, bool escapeDollar)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007f')
            {
                throw new ArgumentException("A unit file value must not contain control characters.", nameof(value));
            }

            switch (c)
            {
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '%':
                    quoted.Append("%%");
                    break;
                case '$' when escapeDollar:
                    quoted.Append("$$");
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
