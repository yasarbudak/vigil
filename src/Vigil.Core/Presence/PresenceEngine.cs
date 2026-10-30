using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Core.Presence;

/// <summary>
/// Decides when to inject and tracks the outcome. It performs no input or OS calls itself:
/// callers feed it observations and report injection results. All time comes from <see cref="TimeProvider"/>.
/// </summary>
public sealed class PresenceEngine
{
    /// <summary>Allowed slack on top of <see cref="VigilOptions.VerifyDelay"/> when judging that the idle counter was reset.</summary>
    internal static readonly TimeSpan VerifyTolerance = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();

    // Replaced as a whole by Reconfigure and never modified afterwards, so a reader sees one consistent set of options.
    private volatile VigilOptions _options;
    private readonly TimeProvider _time;
    private readonly IActivityLog _log;

    private PresenceState _state = PresenceState.Watching;
    private PauseReason? _pauseReason;
    private DegradedReason? _degraded;
    private DateTimeOffset? _pausedUntil;
    private DateTimeOffset? _lastRealActivity;
    private DateTimeOffset? _lastInjection;
    private DateTimeOffset? _lastAttempt;
    private bool _injectedSinceActivity;

    // Latest moment at which Vigil failed to keep the user present: a failed or unverified injection, or an
    // attempt blocked by a missing permission. Time up to the next verified injection is not covered.
    private DateTimeOffset? _lastUnverifiedAt;

    // Whether the last observation could tell if the screen is locked; null until the first observation.
    private bool? _lockStateKnown;

    // Start of the current stretch in which Vigil has been continuously active (not paused, not suspended).
    // Set to the engine's creation time in the constructor, so nothing before the daemon started can ever be
    // counted as covered; null while paused until the next active evaluation sets it.
    private DateTimeOffset? _activeSince;
    private DateTimeOffset? _lastEvaluatedAt;
    private DateOnly _day;
    private int _injectionsToday;
    private TimeSpan _coveredToday;

    public PresenceEngine(VigilOptions options, TimeProvider time, IActivityLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid options: " + string.Join(" ", errors), nameof(options));
        }

        _options = options;
        _time = time;
        _log = log;
        _activeSince = time.GetUtcNow();

        _day = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var totals = log.ReadTotals(_day);
        _injectionsToday = totals.Injections;
        _coveredToday = totals.Covered;
    }

    /// <summary>The options in force now. A caller that needs several values reads this once, so the values belong together.</summary>
    public VigilOptions Options => _options;

    /// <summary>
    /// Replaces the options while the engine runs (the daemon's <c>ReloadConfig</c>). State, counters and a manual pause are kept;
    /// the new rules apply from the next evaluation. The instance must not be changed after it is handed over.
    /// </summary>
    /// <exception cref="ArgumentException">The options are invalid; the engine keeps the old ones.</exception>
    public void Reconfigure(VigilOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid options: " + string.Join(" ", errors), nameof(options));
        }

        lock (_gate)
        {
            _options = options;
        }
    }

    private TimeSpan VerifyBound => _options.VerifyDelay + VerifyTolerance;

    /// <summary>A longer silence between two evaluations means the machine slept or the process was suspended.</summary>
    private TimeSpan MaxPollGap => _options.PollInterval * 3;

    /// <summary>Decides what to do for the given observation.</summary>
    public PresenceDecision Evaluate(PresenceObservation observation)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var local = _time.GetLocalNow();
            RollDayIfNeeded(local);
            ExpireManualPause(now, local);
            TrackRealActivity(now, observation.IdleTime);
            _lockStateKnown = observation.IsScreenLocked is not null;
            var wasSuspended = _lastEvaluatedAt is { } previous && now - previous > MaxPollGap;
            _lastEvaluatedAt = now;

            if (FindPauseReason(local, observation) is { } reason)
            {
                _pauseReason = reason;
                _state = PresenceState.Paused;
                _activeSince = null;
                return new PresenceDecision(_state, false);
            }

            _pauseReason = null;
            if (_activeSince is null || wasSuspended)
            {
                _activeSince = now; // Time spent paused or asleep was not covered by Vigil.
            }

            if (observation.IdleTime < _options.Threshold)
            {
                _state = RestingState();
                return new PresenceDecision(_state, false);
            }

            if (IsRetryGateClosed(now))
            {
                _state = PresenceState.Degraded;
                return new PresenceDecision(_state, false);
            }

            if (!observation.CanPostEvents)
            {
                EnterDegraded(DegradedReason.PermissionMissing, local, "Permission to post input events is missing.");
                return new PresenceDecision(_state, false);
            }

            _lastAttempt = now;
            _state = PresenceState.Idle;
            return new PresenceDecision(_state, true);
        }
    }

    /// <summary>
    /// Reports the outcome of an injection requested by <see cref="Evaluate"/>.
    /// <paramref name="idleAfter"/> is the idle time re-read after <see cref="VigilOptions.VerifyDelay"/>.
    /// </summary>
    public void RecordInjection(InjectionResult result, TimeSpan idleAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var local = _time.GetLocalNow();
            RollDayIfNeeded(local);

            if (!result.Posted)
            {
                EnterDegraded(DegradedReason.InjectionFailed, local, result.Error);
                return;
            }

            if (idleAfter >= VerifyBound)
            {
                EnterDegraded(
                    DegradedReason.VerificationFailed,
                    local,
                    $"Idle time was still {idleAfter.TotalSeconds:0.0}s after injection.");
                return;
            }

            var recovered = _degraded is not null;
            _degraded = null;

            // Time kept alive: since the latest of the last real activity, the previous injection and the
            // start of the current active stretch (so pauses, closed schedule windows and sleep are excluded).
            var coveredFrom = Max(
                Max(_lastInjection ?? DateTimeOffset.MinValue, _lastRealActivity ?? now),
                _activeSince ?? now);

            // Only verified injections cover time. If Vigil failed since the user was last active and since the
            // previous verified injection, nothing between that failure and this first verified injection was
            // kept alive, so this injection covers nothing. Real activity after the failure starts a fresh
            // stretch, which is counted as after any other activity.
            if (_lastUnverifiedAt is { } unverified && unverified >= coveredFrom)
            {
                coveredFrom = now;
            }

            var covered = now > coveredFrom ? now - coveredFrom : TimeSpan.Zero;

            _lastInjection = now;
            _injectedSinceActivity = true;
            _injectionsToday++;
            _coveredToday += covered;

            if (recovered)
            {
                _log.Record(new ActivityEntry(local, ActivityKind.Recovered));
            }

            _log.Record(new ActivityEntry(local, ActivityKind.Injected, covered));
            SettleState();
        }
    }

    public void Pause(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Pause duration must be greater than zero.");
        }

        PauseUntil(_time.GetUtcNow() + duration);
    }

    public void PauseUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (until <= now)
            {
                throw new ArgumentOutOfRangeException(nameof(until), "Pause end must be in the future.");
            }

            _pausedUntil = until;
            _pauseReason = PauseReason.Manual;
            _state = PresenceState.Paused;
            _log.Record(new ActivityEntry(_time.GetLocalNow(), ActivityKind.Paused, Detail: $"until {until:O}"));
        }
    }

    /// <summary>Pauses until the start of the next local day's schedule window (00:00 with the default schedule).</summary>
    public DateTimeOffset PauseUntilTomorrow()
    {
        var localNow = _time.GetLocalNow().DateTime;
        var tomorrow = localNow.Date.AddDays(1) + _options.Schedule.Start.ToTimeSpan();
        var until = new DateTimeOffset(tomorrow, _time.LocalTimeZone.GetUtcOffset(tomorrow));
        PauseUntil(until);
        return until;
    }

    /// <returns>True if a manual pause was active.</returns>
    public bool Resume()
    {
        lock (_gate)
        {
            if (_pausedUntil is null)
            {
                return false;
            }

            ClearManualPause(_time.GetLocalNow());
            return true;
        }
    }

    public PresenceStatus GetStatus()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var local = _time.GetLocalNow();
            RollDayIfNeeded(local);
            ExpireManualPause(now, local);

            return new PresenceStatus(
                _state,
                _pauseReason,
                _degraded,
                _pausedUntil,
                _lastRealActivity,
                _lastInjection,
                _injectionsToday,
                _coveredToday,
                now,
                _options.PauseOnLock,
                _lockStateKnown);
        }
    }

    private PauseReason? FindPauseReason(DateTimeOffset local, PresenceObservation observation)
    {
        if (_pausedUntil is not null)
        {
            return PauseReason.Manual;
        }

        if (!_options.Schedule.IsActive(local.DateTime))
        {
            return PauseReason.OutsideSchedule;
        }

        if (_options.PauseOnLock && observation.IsScreenLocked == true)
        {
            return PauseReason.ScreenLocked;
        }

        if (_options.PauseOnBattery && observation.IsOnBattery == true)
        {
            return PauseReason.OnBattery;
        }

        return null;
    }

    private bool IsRetryGateClosed(DateTimeOffset now) =>
        _degraded is DegradedReason.InjectionFailed or DegradedReason.VerificationFailed &&
        _lastAttempt is { } last &&
        now - last < _options.DegradedRetryInterval;

    /// <summary>
    /// Real activity is whatever the idle counter shows that is not our own injection.
    /// An event within the verification bound of our last injection is treated as ours.
    /// </summary>
    private void TrackRealActivity(DateTimeOffset now, TimeSpan idle)
    {
        var activityAt = now - idle;

        if (_lastInjection is { } injection && activityAt <= injection + VerifyBound)
        {
            return;
        }

        if (_lastRealActivity is { } last && activityAt <= last + VerifyTolerance)
        {
            return;
        }

        _lastRealActivity = activityAt;
        _injectedSinceActivity = false;
    }

    private void EnterDegraded(DegradedReason reason, DateTimeOffset local, string? detail)
    {
        if (_degraded != reason)
        {
            _log.Record(new ActivityEntry(local, ActivityKind.Degraded, Detail: detail ?? reason.ToString()));
        }

        _lastUnverifiedAt = _time.GetUtcNow();
        _degraded = reason;
        SettleState();
    }

    private void ExpireManualPause(DateTimeOffset now, DateTimeOffset local)
    {
        if (_pausedUntil is { } until && until <= now)
        {
            ClearManualPause(local);
        }
    }

    private void ClearManualPause(DateTimeOffset local)
    {
        _pausedUntil = null;
        _pauseReason = null;
        _state = RestingState();
        _log.Record(new ActivityEntry(local, ActivityKind.Resumed));
    }

    private void RollDayIfNeeded(DateTimeOffset local)
    {
        var today = DateOnly.FromDateTime(local.DateTime);
        if (today == _day)
        {
            return;
        }

        _day = today;
        _injectionsToday = 0;
        _coveredToday = TimeSpan.Zero;
    }

    private PresenceState RestingState() =>
        _degraded is not null ? PresenceState.Degraded
        : _injectedSinceActivity ? PresenceState.Injected
        : PresenceState.Watching;

    /// <summary>Updates the state after a result, without overriding a pause that began meanwhile.</summary>
    private void SettleState()
    {
        if (_state != PresenceState.Paused)
        {
            _state = RestingState();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
