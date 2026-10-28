using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Platform;

namespace Vigil.Daemon;

/// <summary>Runs one <see cref="PresenceCycle"/> per poll interval until the host stops.</summary>
internal sealed class PresenceWorker(
    PresenceCycle cycle,
    PresenceEngine engine,
    VigilOptions options,
    PlatformServices platform,
    TimeProvider time,
    ILogger<PresenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Watching idle time every {Poll}s, acting at {Threshold}s idle.",
            options.PollInterval.TotalSeconds,
            options.Threshold.TotalSeconds);

        if (StartupWarnings.LockDetection(options, platform.Session) is { } warning)
        {
            logger.LogWarning("{Warning}", warning);
        }

        using var timer = new PeriodicTimer(options.PollInterval, time);
        var reported = (State: (PresenceState?)null, Reason: (DegradedReason?)null);

        try
        {
            do
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                reported = ReportChange(reported);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cycle.RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failed poll must not end the daemon; the next tick tries again.
            logger.LogError(ex, "Presence cycle failed.");
        }
    }

    private (PresenceState?, DegradedReason?) ReportChange((PresenceState? State, DegradedReason? Reason) previous)
    {
        var status = engine.GetStatus();
        var current = (State: (PresenceState?)status.State, Reason: status.DegradedReason);
        if (current == previous)
        {
            return previous;
        }

        if (status.State == PresenceState.Degraded)
        {
            logger.LogWarning("Degraded: {Reason}. Vigil is not keeping you present.", status.DegradedReason);
        }
        else
        {
            logger.LogInformation("State: {State}{Pause}", status.State, status.PauseReason is { } r ? $" ({r})" : string.Empty);
        }

        return current;
    }
}
