using Vigil.Commands;
using Vigil.Core.Presence;
using Vigil.Ipc;
using Vigil.Tray;

namespace Vigil.Tests;

public class TrayModelTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 9, 10, 0, 0, TimeSpan.Zero);

    private static PresenceStatus Status(
        PresenceState state,
        PauseReason? pause = null,
        DegradedReason? degraded = null,
        DateTimeOffset? until = null,
        int injections = 3,
        TimeSpan? covered = null) =>
        new(state, pause, degraded, until, null, null, injections, covered ?? TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(4), At);

    private static DaemonReply Running(PresenceStatus status) => new(true, true, Status: status);

    [Theory]
    [InlineData(PresenceState.Watching, "Watching")]
    [InlineData(PresenceState.Idle, "Watching")]
    [InlineData(PresenceState.Injected, "Injected")]
    [InlineData(PresenceState.Paused, "Paused")]
    [InlineData(PresenceState.Degraded, "Degraded")]
    public void Each_state_has_its_icon(PresenceState state, string expected)
    {
        var model = TrayModel.From(Running(Status(state, pause: state == PresenceState.Paused ? PauseReason.Manual : null)));

        Assert.Equal(Enum.Parse<TrayIconKind>(expected), model.Icon);
    }

    [Fact]
    public void Every_state_of_the_engine_is_mapped()
    {
        foreach (var state in Enum.GetValues<PresenceState>())
        {
            Assert.True(Enum.IsDefined(TrayModel.IconFor(state)));
        }
    }

    [Fact]
    public void The_lines_use_the_wording_of_vigil_status()
    {
        var status = Status(PresenceState.Degraded, degraded: DegradedReason.PermissionMissing);

        var model = TrayModel.From(Running(status));

        Assert.Equal("State: Degraded (PermissionMissing)", model.StateLine);
        Assert.Equal("Today: 3 injections, 12m 4s covered", model.TodayLine);
        Assert.Equal("Vigil: Degraded (PermissionMissing)", model.ToolTip);
        Assert.Equal(StatusFormatter.Describe(status), model.StateLine["State: ".Length..]);
        Assert.Equal(StatusFormatter.DescribeToday(status), model.TodayLine["Today: ".Length..]);
    }

    [Fact]
    public void A_status_text_from_the_cli_formatter_carries_the_same_lines()
    {
        var status = Status(PresenceState.Paused, pause: PauseReason.OutsideSchedule);
        using var writer = new StringWriter();
        StatusFormatter.Write(writer, status);

        var model = TrayModel.From(Running(status));

        Assert.Contains(model.StateLine["State: ".Length..], writer.ToString(), StringComparison.Ordinal);
        Assert.Contains(model.TodayLine["Today: ".Length..], writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_daemon_can_be_paused()
    {
        var model = TrayModel.From(Running(Status(PresenceState.Watching)));

        Assert.True(model.CanPause);
        Assert.False(model.CanResume);
    }

    [Fact]
    public void Only_a_manual_pause_can_be_resumed()
    {
        Assert.True(TrayModel.From(Running(Status(PresenceState.Paused, PauseReason.Manual, until: At.AddMinutes(15)))).CanResume);
        Assert.False(TrayModel.From(Running(Status(PresenceState.Paused, PauseReason.OutsideSchedule))).CanResume);
        Assert.False(TrayModel.From(Running(Status(PresenceState.Paused, PauseReason.ScreenLocked))).CanResume);
        Assert.False(TrayModel.From(Running(Status(PresenceState.Paused, PauseReason.OnBattery))).CanResume);
    }

    [Fact]
    public void A_manual_pause_shows_when_it_ends()
    {
        var until = new DateTimeOffset(2026, 3, 9, 10, 15, 0, TimeSpan.Zero);

        var model = TrayModel.From(Running(Status(PresenceState.Paused, PauseReason.Manual, until: until)));

        Assert.StartsWith("State: Paused (until ", model.StateLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_daemon_there_is_the_not_running_state_and_no_action()
    {
        var model = TrayModel.From(DaemonReply.Unreachable("The Vigil daemon is not running (no socket at /x/vigil.sock)."));

        Assert.Equal(TrayIconKind.NotRunning, model.Icon);
        Assert.Equal("Vigil is not running", model.StateLine);
        Assert.False(model.CanPause);
        Assert.False(model.CanResume);
        Assert.Equal("Vigil: not running", model.ToolTip); // The reason is for the window, not for a tooltip.
    }

    [Fact]
    public void A_reply_without_a_status_counts_as_not_running()
    {
        var model = TrayModel.From(new DaemonReply(true, true));

        Assert.Equal(TrayIconKind.NotRunning, model.Icon);
    }

    [Fact]
    public void The_menu_commands_are_the_ipc_commands_of_the_cli()
    {
        Assert.Equal(new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(15)), TrayCommands.Pause(PauseChoice.FifteenMinutes));
        Assert.Equal(new IpcRequest(IpcCommand.Pause, TimeSpan.FromHours(1)), TrayCommands.Pause(PauseChoice.OneHour));
        Assert.Equal(new IpcRequest(IpcCommand.PauseUntilTomorrow), TrayCommands.Pause(PauseChoice.UntilTomorrow));
        Assert.Equal(new IpcRequest(IpcCommand.Resume), TrayCommands.Resume());
        Assert.Equal(new IpcRequest(IpcCommand.Status), TrayCommands.Status());
        Assert.Equal(new IpcRequest(IpcCommand.ReloadConfig), TrayCommands.Reload());
    }

    [Fact]
    public void Every_pause_entry_has_a_label_and_a_command()
    {
        foreach (var choice in Enum.GetValues<PauseChoice>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TrayCommands.Label(choice)));
            Assert.True(TrayCommands.Pause(choice).Command is IpcCommand.Pause or IpcCommand.PauseUntilTomorrow);
        }
    }

    [Fact]
    public void An_unknown_pause_entry_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayCommands.Pause((PauseChoice)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayCommands.Label((PauseChoice)99));
    }
}
