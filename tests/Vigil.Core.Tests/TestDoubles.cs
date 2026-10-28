using Vigil.Core.Abstractions;

namespace Vigil.Core.Tests;

/// <summary>Time that only moves when the test says so. Timers fire during <see cref="Advance"/>. Local time is UTC unless a zone is given.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_due is not { } due || due > now)
            {
                return;
            }

            _due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? due + _period : null;
            callback(state);
        }

        public void Dispose() => owner._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A simulated machine: idle time is "now minus the last input", and injection resets it only when <see cref="Works"/>.</summary>
internal sealed class FakeInput(ManualTimeProvider time) : IIdleSource, IInputInjector
{
    private DateTimeOffset _lastInput = time.GetUtcNow();

    public bool Works { get; set; } = true;

    public InjectionResult? ForcedResult { get; set; }

    public int InjectCalls { get; private set; }

    public TimeSpan GetIdleTime() => time.GetUtcNow() - _lastInput;

    /// <summary>The user touches the keyboard now.</summary>
    public void UserActivity() => _lastInput = time.GetUtcNow();

    public InjectionResult Inject()
    {
        InjectCalls++;
        if (ForcedResult is { } forced)
        {
            return forced;
        }

        if (Works)
        {
            _lastInput = time.GetUtcNow();
        }

        return InjectionResult.Success;
    }
}

internal sealed class FakeSession : ISessionState, IPermissionProbe
{
    public bool? Locked { get; set; } = false;

    public bool? OnBattery { get; set; } = false;

    public bool PermissionGranted { get; set; } = true;

    public bool? IsScreenLocked() => Locked;

    public bool? IsOnBattery() => OnBattery;

    public bool CanPostEvents() => PermissionGranted;
}

internal sealed class FakeActivityLog : IActivityLog
{
    public List<ActivityEntry> Entries { get; } = [];

    public DailyTotals Seed { get; set; }

    public void Record(ActivityEntry entry) => Entries.Add(entry);

    public DailyTotals ReadTotals(DateOnly day) => Seed;
}
