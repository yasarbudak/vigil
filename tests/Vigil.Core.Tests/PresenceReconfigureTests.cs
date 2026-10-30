using Vigil.Core.Options;
using Vigil.Core.Presence;

namespace Vigil.Core.Tests;

public class PresenceReconfigureTests
{
    [Fact]
    public async Task A_lower_threshold_applies_to_the_next_poll()
    {
        var rig = new PresenceRig();
        await rig.RunForAsync(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Input.InjectCalls);

        rig.Engine.Reconfigure(new VigilOptions { Threshold = TimeSpan.FromMinutes(2) });
        await rig.PollAfterAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, rig.Input.InjectCalls);
    }

    [Fact]
    public void Options_returns_the_instance_in_force()
    {
        var rig = new PresenceRig();
        var replacement = new VigilOptions { PauseOnBattery = true };

        rig.Engine.Reconfigure(replacement);

        Assert.Same(replacement, rig.Engine.Options);
    }

    [Fact]
    public async Task Invalid_options_are_refused_and_the_engine_keeps_the_old_ones()
    {
        var rig = new PresenceRig();
        var before = rig.Engine.Options;

        var error = Assert.Throws<ArgumentException>(() => rig.Engine.Reconfigure(new VigilOptions { Threshold = TimeSpan.Zero }));

        Assert.Contains("Threshold", error.Message, StringComparison.Ordinal);
        Assert.Same(before, rig.Engine.Options);
        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        Assert.Equal(1, rig.Input.InjectCalls);
    }

    [Fact]
    public void Null_options_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new PresenceRig().Engine.Reconfigure(null!));
    }

    [Fact]
    public async Task A_manual_pause_and_the_counters_survive_a_reconfiguration()
    {
        var rig = new PresenceRig();
        await rig.RunForAsync(TimeSpan.FromMinutes(4));
        var injections = rig.Status.InjectionsToday;
        rig.Engine.Pause(TimeSpan.FromHours(1));

        rig.Engine.Reconfigure(new VigilOptions { PauseOnLock = false });

        var status = rig.Status;
        Assert.Equal(PresenceState.Paused, status.State);
        Assert.Equal(PauseReason.Manual, status.PauseReason);
        Assert.Equal(injections, status.InjectionsToday);
        Assert.False(status.PauseOnLock);
    }

    [Fact]
    public async Task A_closed_schedule_window_pauses_after_the_reconfiguration()
    {
        var rig = new PresenceRig(); // Monday 10:00.
        await rig.PollAfterAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(PresenceState.Paused, rig.Status.State);

        rig.Engine.Reconfigure(new VigilOptions { Schedule = new ScheduleOptions { ActiveDays = [DayOfWeek.Saturday] } });
        await rig.PollAfterAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PauseReason.OutsideSchedule, rig.Status.PauseReason);
    }

    [Fact]
    public async Task The_verification_wait_follows_the_reconfigured_delay()
    {
        var rig = new PresenceRig();
        rig.Engine.Reconfigure(new VigilOptions { VerifyDelay = TimeSpan.FromSeconds(2) });
        rig.Input.IdleFor(TimeSpan.FromMinutes(5));

        var poll = rig.Cycle.RunOnceAsync(CancellationToken.None);
        Assert.False(poll.IsCompleted); // Posted, now waiting to verify.
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(poll.IsCompleted); // The default half-second delay would be over by now.
        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        await poll;

        Assert.Equal(PresenceState.Injected, rig.Status.State);
    }
}
