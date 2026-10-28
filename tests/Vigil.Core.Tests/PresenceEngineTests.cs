using Vigil.Core.Abstractions;
using Vigil.Core.Presence;

namespace Vigil.Core.Tests;

public class PresenceEngineTests
{
    private static PresenceObservation Idle(TimeSpan idle) => new(idle, false, false, true);

    [Fact]
    public void Idle_below_threshold_means_Watching_and_no_injection()
    {
        var rig = new PresenceRig();

        var decision = rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(3.9)));

        Assert.False(decision.ShouldInject);
        Assert.Equal(PresenceState.Watching, decision.State);
    }

    [Fact]
    public void Idle_exactly_at_threshold_requests_an_injection_and_reports_Idle()
    {
        var rig = new PresenceRig();

        var decision = rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(4)));

        Assert.True(decision.ShouldInject);
        Assert.Equal(PresenceState.Idle, decision.State);
    }

    [Fact]
    public void Idle_after_injection_just_inside_the_bound_verifies()
    {
        var rig = new PresenceRig();
        rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(4)));

        rig.Engine.RecordInjection(InjectionResult.Success, TimeSpan.FromSeconds(1.4));

        Assert.Equal(PresenceState.Injected, rig.Status.State);
    }

    [Fact]
    public void Idle_after_injection_at_the_bound_fails_verification()
    {
        var rig = new PresenceRig();
        rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(4)));

        rig.Engine.RecordInjection(InjectionResult.Success, TimeSpan.FromSeconds(1.5));

        Assert.Equal(PresenceState.Degraded, rig.Status.State);
        Assert.Equal(DegradedReason.VerificationFailed, rig.Status.DegradedReason);
    }

    [Fact]
    public void Pause_rejects_non_positive_durations()
    {
        var rig = new PresenceRig();

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Engine.Pause(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Engine.Pause(TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void PauseUntil_rejects_a_time_in_the_past()
    {
        var rig = new PresenceRig();

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Engine.PauseUntil(rig.Time.GetUtcNow().AddSeconds(-1)));
    }

    [Fact]
    public void PauseUntil_reports_the_end_in_the_status_and_Paused_state_at_once()
    {
        var rig = new PresenceRig();
        var until = rig.Time.GetUtcNow().AddHours(2);

        rig.Engine.PauseUntil(until);

        Assert.Equal(PresenceState.Paused, rig.Status.State);
        Assert.Equal(until, rig.Status.PausedUntil);
    }

    [Fact]
    public void PauseUntilTomorrow_ends_at_next_local_midnight_by_default()
    {
        var rig = new PresenceRig(start: new DateTimeOffset(2026, 3, 9, 22, 15, 0, TimeSpan.Zero));

        var until = rig.Engine.PauseUntilTomorrow();

        Assert.Equal(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), until);
    }

    [Fact]
    public void PauseUntilTomorrow_ends_at_the_schedule_start_when_one_is_set()
    {
        var rig = new PresenceRig(o =>
        {
            o.Schedule.Start = new TimeOnly(9, 0);
            o.Schedule.End = new TimeOnly(17, 0);
        });

        var until = rig.Engine.PauseUntilTomorrow();

        Assert.Equal(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero), until);
    }

    [Fact]
    public void Manual_pause_wins_over_every_other_reason()
    {
        var rig = new PresenceRig();
        rig.Session.Locked = true;
        rig.Engine.Pause(TimeSpan.FromHours(1));

        var decision = rig.Engine.Evaluate(new PresenceObservation(TimeSpan.FromMinutes(10), true, true, true));

        Assert.Equal(PresenceState.Paused, decision.State);
        Assert.Equal(PauseReason.Manual, rig.Status.PauseReason);
    }

    [Fact]
    public void Degraded_is_remembered_across_a_pause()
    {
        var rig = new PresenceRig();
        rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(4)));
        rig.Engine.RecordInjection(InjectionResult.Failure("x"), TimeSpan.FromMinutes(4));
        rig.Engine.Pause(TimeSpan.FromMinutes(1));
        Assert.Equal(PresenceState.Paused, rig.Status.State);

        Assert.True(rig.Engine.Resume());

        Assert.Equal(PresenceState.Degraded, rig.Status.State);
    }

    [Fact]
    public void Verified_injection_is_logged_with_the_time_it_covered()
    {
        var rig = new PresenceRig();
        rig.Time.Advance(TimeSpan.FromMinutes(4));
        rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(4)));

        rig.Engine.RecordInjection(InjectionResult.Success, TimeSpan.Zero);

        var entry = Assert.Single(rig.Log.Entries, e => e.Kind == ActivityKind.Injected);
        Assert.Equal(TimeSpan.FromMinutes(4), entry.Covered);
    }

    [Fact]
    public void Time_idle_before_the_engine_started_is_not_counted_as_covered()
    {
        // Real case: the user had been idle for 10 minutes when the daemon started; the first injection came
        // about one second later and was logged as covering 00:09:58.
        var rig = new PresenceRig();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Engine.Evaluate(Idle(TimeSpan.FromMinutes(10)));

        rig.Engine.RecordInjection(InjectionResult.Success, TimeSpan.Zero);

        var entry = Assert.Single(rig.Log.Entries, e => e.Kind == ActivityKind.Injected);
        Assert.True(entry.Covered <= TimeSpan.FromSeconds(1), $"Covered {entry.Covered} exceeds the time since start.");
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Status.CoveredToday);
    }

    [Fact]
    public void Constructor_rejects_invalid_options()
    {
        var options = new Options.VigilOptions { Threshold = TimeSpan.Zero };

        Assert.Throws<ArgumentException>(() =>
            new PresenceEngine(options, new ManualTimeProvider(PresenceRig.DefaultStart), new FakeActivityLog()));
    }
}
