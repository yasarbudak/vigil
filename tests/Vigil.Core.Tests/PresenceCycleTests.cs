using Vigil.Core.Abstractions;
using Vigil.Core.Presence;

namespace Vigil.Core.Tests;

public class PresenceCycleTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Below_threshold_does_not_inject()
    {
        var rig = new PresenceRig();

        await rig.RunForAsync(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(50));

        Assert.Equal(0, rig.Input.InjectCalls);
        Assert.Equal(PresenceState.Watching, rig.Status.State);
    }

    [Fact]
    public async Task Threshold_reached_injects_once_and_reports_Injected()
    {
        var rig = new PresenceRig();

        await rig.RunForAsync(TimeSpan.FromMinutes(4));

        Assert.Equal(1, rig.Input.InjectCalls);
        var status = rig.Status;
        Assert.Equal(PresenceState.Injected, status.State);
        Assert.Equal(1, status.InjectionsToday);
        Assert.NotNull(status.LastInjection);
        Assert.InRange(status.CoveredToday, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Injection_that_does_not_reset_idle_makes_state_Degraded()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;

        await rig.RunForAsync(TimeSpan.FromMinutes(4));

        var status = rig.Status;
        Assert.Equal(PresenceState.Degraded, status.State);
        Assert.Equal(DegradedReason.VerificationFailed, status.DegradedReason);
        Assert.Equal(0, status.InjectionsToday);
        Assert.Contains(rig.Log.Entries, e => e.Kind == ActivityKind.Degraded);
    }

    [Fact]
    public async Task Failed_post_makes_state_Degraded_with_the_error()
    {
        var rig = new PresenceRig();
        rig.Input.ForcedResult = InjectionResult.Failure("blocked");

        await rig.RunForAsync(TimeSpan.FromMinutes(4));

        Assert.Equal(DegradedReason.InjectionFailed, rig.Status.DegradedReason);
        Assert.Contains(rig.Log.Entries, e => e.Kind == ActivityKind.Degraded && e.Detail == "blocked");
    }

    [Fact]
    public async Task Degraded_retries_only_after_the_retry_interval()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        var callsAfterFirstFailure = rig.Input.InjectCalls;

        await rig.RunForAsync(TimeSpan.FromSeconds(50));
        Assert.Equal(callsAfterFirstFailure, rig.Input.InjectCalls);

        await rig.RunForAsync(Minute);
        Assert.True(rig.Input.InjectCalls > callsAfterFirstFailure);
    }

    [Fact]
    public async Task Degraded_recovers_when_a_later_injection_verifies()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        Assert.Equal(PresenceState.Degraded, rig.Status.State);

        rig.Input.Works = true;
        await rig.RunForAsync(TimeSpan.FromMinutes(2));

        var status = rig.Status;
        Assert.Equal(PresenceState.Injected, status.State);
        Assert.Null(status.DegradedReason);
        Assert.Contains(rig.Log.Entries, e => e.Kind == ActivityKind.Recovered);
    }

    [Fact]
    public async Task Missing_permission_degrades_without_injecting_and_recovers_when_granted()
    {
        var rig = new PresenceRig();
        rig.Session.PermissionGranted = false;

        await rig.RunForAsync(TimeSpan.FromMinutes(4));

        Assert.Equal(0, rig.Input.InjectCalls);
        Assert.Equal(DegradedReason.PermissionMissing, rig.Status.DegradedReason);

        rig.Session.PermissionGranted = true;
        await rig.RunForAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PresenceState.Injected, rig.Status.State);
        Assert.Null(rig.Status.DegradedReason);
    }

    [Fact]
    public async Task Stays_Injected_while_user_is_away_and_injects_again_after_another_threshold()
    {
        var rig = new PresenceRig();

        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        await rig.RunForAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(1, rig.Input.InjectCalls);
        Assert.Equal(PresenceState.Injected, rig.Status.State);

        await rig.RunForAsync(TimeSpan.FromMinutes(2.5));
        Assert.Equal(2, rig.Input.InjectCalls);
        Assert.InRange(rig.Status.CoveredToday, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(9.5));
    }

    [Fact]
    public async Task Real_activity_after_an_injection_returns_to_Watching_and_is_recorded()
    {
        var rig = new PresenceRig();
        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        await rig.RunForAsync(TimeSpan.FromSeconds(30));

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Input.UserActivity();
        var activityAt = rig.Time.GetUtcNow();
        await rig.PollAfterAsync(TimeSpan.FromSeconds(5));

        var status = rig.Status;
        Assert.Equal(PresenceState.Watching, status.State);
        Assert.Equal(activityAt, status.LastRealActivity);
    }

    [Fact]
    public async Task Own_injection_is_not_reported_as_real_activity()
    {
        var rig = new PresenceRig();
        var originalActivity = rig.Time.GetUtcNow();

        await rig.RunForAsync(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));

        Assert.Equal(originalActivity, rig.Status.LastRealActivity);
    }

    [Fact]
    public async Task Manual_pause_blocks_injection_until_it_expires()
    {
        var rig = new PresenceRig();
        rig.Engine.Pause(TimeSpan.FromMinutes(15));

        await rig.RunForAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(0, rig.Input.InjectCalls);
        Assert.Equal(PresenceState.Paused, rig.Status.State);
        Assert.Equal(PauseReason.Manual, rig.Status.PauseReason);

        await rig.RunForAsync(TimeSpan.FromMinutes(6));
        Assert.True(rig.Input.InjectCalls >= 1);
        Assert.NotEqual(PresenceState.Paused, rig.Status.State);
    }

    [Fact]
    public async Task Resume_ends_a_pause_immediately()
    {
        var rig = new PresenceRig();
        rig.Engine.Pause(TimeSpan.FromHours(1));
        await rig.RunForAsync(TimeSpan.FromMinutes(5));

        Assert.True(rig.Engine.Resume());
        await rig.RunForAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, rig.Input.InjectCalls);
        Assert.Contains(rig.Log.Entries, e => e.Kind == ActivityKind.Resumed);
    }

    [Fact]
    public async Task Resume_without_a_pause_reports_false()
    {
        var rig = new PresenceRig();
        await rig.RunForAsync(TimeSpan.FromSeconds(10));

        Assert.False(rig.Engine.Resume());
    }

    [Fact]
    public async Task Locked_screen_pauses_when_PauseOnLock_is_true()
    {
        var rig = new PresenceRig();
        rig.Session.Locked = true;

        await rig.RunForAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, rig.Input.InjectCalls);
        Assert.Equal(PauseReason.ScreenLocked, rig.Status.PauseReason);
    }

    [Fact]
    public async Task Locked_screen_is_ignored_when_PauseOnLock_is_false()
    {
        var rig = new PresenceRig(o => o.PauseOnLock = false);
        rig.Session.Locked = true;

        await rig.RunForAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(1, rig.Input.InjectCalls);
    }

    [Fact]
    public async Task Unknown_lock_state_does_not_pause()
    {
        var rig = new PresenceRig();
        rig.Session.Locked = null;

        await rig.RunForAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(1, rig.Input.InjectCalls);
    }

    [Fact]
    public async Task Battery_pauses_only_when_PauseOnBattery_is_true()
    {
        var onAc = new PresenceRig();
        onAc.Session.OnBattery = true;
        await onAc.RunForAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, onAc.Input.InjectCalls);

        var paused = new PresenceRig(o => o.PauseOnBattery = true);
        paused.Session.OnBattery = true;
        await paused.RunForAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(0, paused.Input.InjectCalls);
        Assert.Equal(PauseReason.OnBattery, paused.Status.PauseReason);
    }

    [Fact]
    public async Task Outside_the_schedule_does_not_inject_and_resumes_when_the_window_opens()
    {
        var rig = new PresenceRig(o =>
        {
            o.Schedule.Start = new TimeOnly(10, 30);
            o.Schedule.End = new TimeOnly(18, 0);
        });

        await rig.RunForAsync(TimeSpan.FromMinutes(20));
        Assert.Equal(0, rig.Input.InjectCalls);
        Assert.Equal(PauseReason.OutsideSchedule, rig.Status.PauseReason);

        await rig.RunForAsync(TimeSpan.FromMinutes(11));
        Assert.True(rig.Input.InjectCalls >= 1);
    }

    [Fact]
    public async Task Counters_restart_on_a_new_local_day()
    {
        var rig = new PresenceRig(start: new DateTimeOffset(2026, 3, 9, 23, 50, 0, TimeSpan.Zero));
        await rig.RunForAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, rig.Status.InjectionsToday);

        rig.Time.Advance(TimeSpan.FromMinutes(6)); // now 00:05 the next day
        rig.Input.UserActivity();
        await rig.PollAfterAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, rig.Status.InjectionsToday);
        Assert.Equal(TimeSpan.Zero, rig.Status.CoveredToday);
    }

    [Fact]
    public void Todays_counters_are_restored_from_the_activity_log()
    {
        var rig = new PresenceRig(seed: new DailyTotalsSeed(7, TimeSpan.FromMinutes(28)));

        Assert.Equal(7, rig.Status.InjectionsToday);
        Assert.Equal(TimeSpan.FromMinutes(28), rig.Status.CoveredToday);
    }

    [Fact]
    public async Task Time_spent_paused_is_not_counted_as_covered()
    {
        var rig = new PresenceRig();
        rig.Engine.Pause(TimeSpan.FromHours(2));

        await rig.RunForAsync(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(30));

        Assert.True(rig.Input.InjectCalls >= 1);
        Assert.InRange(rig.Status.CoveredToday, TimeSpan.Zero, rig.Options.Threshold);
    }

    [Fact]
    public async Task Time_outside_the_schedule_is_not_counted_as_covered()
    {
        var rig = new PresenceRig(o =>
        {
            o.Schedule.Start = new TimeOnly(11, 0);
            o.Schedule.End = new TimeOnly(18, 0);
        });

        await rig.RunForAsync(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        Assert.True(rig.Input.InjectCalls >= 1);
        Assert.InRange(rig.Status.CoveredToday, TimeSpan.Zero, rig.Options.Threshold);
    }

    [Fact]
    public async Task Time_with_a_locked_screen_is_not_counted_as_covered()
    {
        var rig = new PresenceRig();
        rig.Session.Locked = true;
        await rig.RunForAsync(TimeSpan.FromHours(1));

        rig.Session.Locked = false;
        await rig.RunForAsync(TimeSpan.FromSeconds(30));

        Assert.True(rig.Input.InjectCalls >= 1);
        Assert.InRange(rig.Status.CoveredToday, TimeSpan.Zero, rig.Options.Threshold);
    }

    [Fact]
    public async Task Time_idle_before_the_daemon_started_is_not_counted_as_covered()
    {
        var rig = new PresenceRig();
        rig.Input.IdleFor(TimeSpan.FromMinutes(10));

        await rig.PollAfterAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, rig.Input.InjectCalls);
        var entry = Assert.Single(rig.Log.Entries, e => e.Kind == ActivityKind.Injected);
        Assert.True(entry.Covered <= TimeSpan.FromSeconds(2), $"Covered {entry.Covered} exceeds the time since start.");
        Assert.True(rig.Status.CoveredToday <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Time_while_the_machine_slept_is_not_counted_as_covered()
    {
        var rig = new PresenceRig();
        await rig.RunForAsync(TimeSpan.FromSeconds(30));

        // No polls at all for three hours: sleep or suspension.
        await rig.PollAfterAsync(TimeSpan.FromHours(3));

        Assert.Equal(1, rig.Input.InjectCalls);
        Assert.InRange(rig.Status.CoveredToday, TimeSpan.Zero, rig.Options.Threshold);
    }

    [Fact]
    public async Task Recovery_after_a_degraded_stretch_does_not_count_the_degraded_time_as_covered()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(14)); // Degraded from minute 4; the user never came back.
        Assert.Equal(PresenceState.Degraded, rig.Status.State);
        Assert.Equal(TimeSpan.Zero, rig.Status.CoveredToday);

        rig.Input.Works = true;
        await rig.RunForAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(PresenceState.Injected, rig.Status.State);
        var recovery = Assert.Single(rig.Log.Entries, e => e.Kind == ActivityKind.Injected);
        Assert.Equal(TimeSpan.Zero, recovery.Covered);
        Assert.Equal(TimeSpan.Zero, rig.Status.CoveredToday);
    }

    [Fact]
    public async Task Time_after_the_recovering_injection_is_counted_again()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(14));
        rig.Input.Works = true;
        await rig.RunForAsync(TimeSpan.FromMinutes(2));
        var recoveredAt = rig.Time.GetUtcNow();

        await rig.RunForAsync(TimeSpan.FromMinutes(6));

        var second = rig.Log.Entries.Where(e => e.Kind == ActivityKind.Injected).ElementAt(1);
        Assert.InRange(second.Covered!.Value, rig.Options.Threshold - TimeSpan.FromMinutes(1), rig.Options.Threshold + TimeSpan.FromMinutes(1));
        Assert.True(rig.Status.LastInjection > recoveredAt);
    }

    [Fact]
    public async Task Time_with_a_missing_permission_is_not_counted_as_covered_after_it_is_granted()
    {
        var rig = new PresenceRig();
        rig.Session.PermissionGranted = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(DegradedReason.PermissionMissing, rig.Status.DegradedReason);

        rig.Session.PermissionGranted = true;
        await rig.RunForAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PresenceState.Injected, rig.Status.State);
        Assert.Equal(TimeSpan.Zero, rig.Status.CoveredToday);
    }

    [Fact]
    public async Task Activity_after_a_failure_starts_a_fresh_stretch_that_is_counted_normally()
    {
        var rig = new PresenceRig();
        rig.Input.Works = false;
        await rig.RunForAsync(TimeSpan.FromMinutes(10)); // Degraded.

        rig.Time.Advance(TimeSpan.FromSeconds(1)); // Strictly after the last failed attempt.
        rig.Input.UserActivity();
        rig.Input.Works = true;
        await rig.RunForAsync(TimeSpan.FromMinutes(5)); // The user is idle again from the activity; injection after the threshold.

        Assert.Equal(PresenceState.Injected, rig.Status.State);
        Assert.InRange(rig.Status.CoveredToday, rig.Options.Threshold - TimeSpan.FromSeconds(10), rig.Options.Threshold + TimeSpan.FromSeconds(10));
    }
}
