using Vigil.Commands;
using Vigil.Core.Presence;

namespace Vigil.Tray;

/// <summary>Which icon the tray shows. Idle (an injection is due for a moment) counts as <see cref="Watching"/>.</summary>
internal enum TrayIconKind
{
    Watching,
    Injected,
    Paused,
    Degraded,
    NotRunning,
}

/// <summary>What the tray shows for one daemon reply: the icon, the two menu lines, the tooltip and which actions are possible.</summary>
/// <param name="CanPause">Pausing needs a daemon that answers.</param>
/// <param name="CanResume">Only a manual pause can be ended; the schedule, the lock and the battery pause on their own.</param>
internal sealed record TrayModel(TrayIconKind Icon, string StateLine, string TodayLine, string ToolTip, bool CanPause, bool CanResume)
{
    public static TrayModel NotRunning(string? reason = null) => new(
        TrayIconKind.NotRunning,
        "Vigil is not running",
        "Today: not available",
        reason is null ? "Vigil: not running" : $"Vigil: not running ({reason})",
        CanPause: false,
        CanResume: false);

    /// <summary>
    /// The wording is <see cref="StatusFormatter"/>'s, the same as <c>vigil status</c> prints, so the tray and the CLI never
    /// disagree about what a state is called.
    /// </summary>
    public static TrayModel From(DaemonReply reply)
    {
        if (reply is not { Reachable: true, Ok: true, Status: { } status })
        {
            // Not reachable, or reachable but without a status: either way there is nothing to show but "not running".
            return NotRunning(reply.Reachable ? reply.Error : null);
        }

        var state = StatusFormatter.Describe(status);
        return new TrayModel(
            IconFor(status.State),
            $"State: {state}",
            $"Today: {StatusFormatter.DescribeToday(status)}",
            $"Vigil: {state}",
            CanPause: true,
            CanResume: status is { State: PresenceState.Paused, PauseReason: PauseReason.Manual });
    }

    public static TrayIconKind IconFor(PresenceState state) => state switch
    {
        PresenceState.Injected => TrayIconKind.Injected,
        PresenceState.Paused => TrayIconKind.Paused,
        PresenceState.Degraded => TrayIconKind.Degraded,
        _ => TrayIconKind.Watching,
    };
}
