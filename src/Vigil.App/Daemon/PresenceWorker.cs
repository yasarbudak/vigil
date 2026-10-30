using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Activity;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Platform;

namespace Vigil.Daemon;

/// <summary>Runs one <see cref="PresenceCycle"/> per poll interval until the host stops.</summary>
internal sealed class PresenceWorker(
    PresenceCycle cycle,
    PresenceEngine engine,
    ActivityLogRetention retention,
    VigilOptions options,
    PlatformServices platform,
    TimeProvider time,
    ILogger<PresenceWorker> logger) : BackgroundService
{
    // Seeded at startup, where the lock state is read once and reported by the startup warning below.
    private LockDetectionMonitor _lockDetection = new(initiallyKnown: true);

    private readonly RepeatedFailureTracker _cycleFailures = new();

    /// <summary>
    /// Logs the startup lines here, not in <see cref="ExecuteAsync"/>: since .NET 10 the host runs ExecuteAsync on a
    /// thread-pool thread after StartAsync has returned, so a quick stop could cancel it before it logged anything
    /// and the lock-detection warning would be lost. StartAsync completes them before the host reports started.
    /// The activity log cleanup due at daemon start runs here for the same reason; later runs (day rollover) stay
    /// in the poll loop.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The platform says it cannot work in this process (Linux without a graphical session).</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (platform.StartupProblem is { } problem)
        {
            throw new PlatformNotSupportedException(problem);
        }

        if (platform.Summary is { } summary)
        {
            logger.LogInformation("Platform: {Summary}.", summary);
        }

        logger.LogInformation(
            "Watching idle time every {Poll}s, acting at {Threshold}s idle.",
            options.PollInterval.TotalSeconds,
            options.Threshold.TotalSeconds);

        var lockState = platform.Session.IsScreenLocked();
        _lockDetection = new LockDetectionMonitor(initiallyKnown: lockState is not null);
        if (StartupWarnings.LockDetection(options, lockState) is { } warning)
        {
            logger.LogWarning("{Warning}", warning);
        }

        RunRetention();

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, time);
        var reported = (State: (PresenceState?)null, Reason: (DegradedReason?)null);

        try
        {
            do
            {
                RunRetention();
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                reported = ReportChange(reported);
                ReportLockDetection();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void RunRetention()
    {
        try
        {
            retention.RunIfDue();
        }
        catch (Exception ex)
        {
            // Housekeeping must never stop the daemon from doing its job.
            logger.LogError(ex, "Activity log cleanup failed.");
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cycle.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            if (_cycleFailures.Recovered())
            {
                logger.LogInformation("Presence cycle works again.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failed poll must not end the daemon; the next tick tries again. The same failure at every poll is logged once.
            if (_cycleFailures.ShouldLog(ex))
            {
                logger.LogError(ex, "Presence cycle failed (the same failure is not logged again until it changes or stops).");
            }
        }
    }

    /// <summary>
    /// The engine keeps injecting while the lock state is unknown (as at startup); this makes the loss and the
    /// recovery visible, once per transition.
    /// </summary>
    private void ReportLockDetection()
    {
        if (!options.PauseOnLock || engine.GetStatus().LockStateKnown is not { } known)
        {
            return;
        }

        switch (_lockDetection.Observe(known))
        {
            case LockDetectionChange.Lost:
                logger.LogWarning("{Warning}", StartupWarnings.LockDetectionLost);
                break;
            case LockDetectionChange.Restored:
                logger.LogInformation("{Message}", StartupWarnings.LockDetectionRestored);
                break;
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
