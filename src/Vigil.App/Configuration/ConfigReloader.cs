using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Activity;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Platform.Linux;

namespace Vigil.Configuration;

/// <summary>What <see cref="IConfigReloader.Reload"/> did.</summary>
/// <param name="Error">Why nothing was changed: the file is unreadable or not valid. Null when the new configuration was applied.</param>
/// <param name="RestartRequired">Keys that the running daemon cannot take over; they apply after a restart.</param>
internal sealed record ReloadResult(string? Error, IReadOnlyList<string> RestartRequired)
{
    public bool Ok => Error is null;

    public static ReloadResult Failed(string error) => new(error, []);
}

/// <summary>Applies config.json to the running daemon (<see cref="Ipc.IpcCommand.ReloadConfig"/>).</summary>
internal interface IConfigReloader
{
    ReloadResult Reload();
}

/// <summary>
/// Reads config.json again with the loader the daemon starts with (<see cref="VigilConfiguration.LoadValidated"/>: the same
/// binding, the same rules) and, only when the file is valid, applies it in place. A file that is not valid changes nothing.
/// </summary>
/// <remarks>
/// What is applied: every <c>Vigil.*</c> key (the engine takes the new options, <see cref="PresenceEngine.Reconfigure"/>; the
/// poll loop and the verification wait read them from it), the activity log (a new implementation when <c>Enabled</c> or
/// <c>RetentionDays</c> changed; a shorter retention deletes files at the next scheduled cleanup, daemon start or midnight, not at
/// once), and the injection strategy of the running OS (the injectors read their options object at every injection, so it is
/// updated in place). What is not: <c>Linux.Backend</c> (the input backend, and with it a portal session or a virtual device, is
/// built once at start) and, on the <c>Uinput</c> backend, <c>Linux.Strategy</c> (the virtual device is created for one strategy).
/// Those are reported in <see cref="ReloadResult.RestartRequired"/> and keep their running value, so what the daemon runs and what
/// it reports stay consistent. Not applied is not lost: config.json holds the new value for the next start.
/// </remarks>
/// <param name="running">The settings the daemon started with; its platform option objects are the ones the injectors hold.</param>
/// <param name="activityLogFactory">Builds the activity log for the given options (the real one, or none when disabled).</param>
internal sealed class ConfigReloader(
    VigilPaths paths,
    PresenceEngine engine,
    ReloadableActivityLog activityLog,
    Func<ActivityLogOptions, IActivityLog> activityLogFactory,
    VigilSettings running,
    ILogger<ConfigReloader>? logger = null) : IConfigReloader
{
    private readonly object _gate = new();
    private readonly ILogger _log = logger ?? NullLogger<ConfigReloader>.Instance;

    public ReloadResult Reload()
    {
        lock (_gate)
        {
            VigilSettings fresh;
            try
            {
                fresh = VigilConfiguration.LoadValidated(paths.ConfigPath);
            }
            catch (ConfigurationException ex)
            {
                _log.LogWarning("Configuration reload refused, nothing changed: {Message}", ex.Message);
                return ReloadResult.Failed(ex.Message);
            }

            engine.Reconfigure(fresh.Vigil);
            activityLog.Replace(activityLogFactory(fresh.Vigil.ActivityLog));

            running.MacOS.Strategy = fresh.MacOS.Strategy;
            running.Windows.Strategy = fresh.Windows.Strategy;

            var restart = new List<string>();
            if (fresh.Linux.Backend != running.Linux.Backend)
            {
                restart.Add("Linux.Backend");
            }

            if (running.Linux.Backend == InputBackend.Uinput && fresh.Linux.Strategy != running.Linux.Strategy)
            {
                restart.Add("Linux.Strategy");
            }
            else
            {
                running.Linux.Strategy = fresh.Linux.Strategy;
            }

            if (restart.Count > 0)
            {
                _log.LogInformation("Configuration reloaded; restart the daemon to apply: {Keys}.", string.Join(", ", restart));
            }
            else
            {
                _log.LogInformation("Configuration reloaded.");
            }

            return new ReloadResult(null, restart);
        }
    }
}
