using System.Globalization;
using Vigil.Core.Presence;

namespace Vigil.Commands;

internal static class StatusFormatter
{
    public static void Write(TextWriter output, PresenceStatus status, string? platform = null)
    {
        output.WriteLine($"State:           {Describe(status)}");
        output.WriteLine($"Last activity:   {FormatPoint(status.LastRealActivity, status.At)}");
        output.WriteLine($"Last injection:  {FormatPoint(status.LastInjection, status.At)}");
        output.WriteLine($"Today:           {DescribeToday(status)}");
        output.WriteLine($"Screen lock:     {DescribeLockDetection(status)}");
        if (platform is not null)
        {
            output.WriteLine($"Platform:        {platform}");
        }
    }

    /// <summary>The counters of the day, as the <c>Today</c> line of <c>vigil status</c> shows them (also used by the tray).</summary>
    public static string DescribeToday(PresenceStatus status) =>
        $"{status.InjectionsToday} injections, {FormatDuration(status.CoveredToday)} covered";

    private static string DescribeLockDetection(PresenceStatus status) => (status.PauseOnLock, status.LockStateKnown) switch
    {
        (false, _) => "not used (PauseOnLock is off)",
        (true, true) => "detected, pausing while locked",
        (true, false) => "UNKNOWN: cannot tell if the screen is locked, NOT pausing while locked",
        (true, null) => "not checked yet",
    };

    /// <summary>The state with its reason, as the <c>State</c> line of <c>vigil status</c> shows it (also used by the tray).</summary>
    public static string Describe(PresenceStatus status) => status.State switch
    {
        PresenceState.Paused when status.PausedUntil is { } until =>
            $"Paused (until {until.ToLocalTime():HH:mm:ss})",
        PresenceState.Paused => $"Paused ({status.PauseReason})",
        PresenceState.Degraded => $"Degraded ({status.DegradedReason})",
        _ => status.State.ToString(),
    };

    private static string FormatPoint(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value
            ? $"{value.ToLocalTime():HH:mm:ss} ({FormatDuration(now - value)} ago)"
            : "none yet";

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m")
        : duration.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}m {duration.Seconds}s")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)duration.TotalSeconds)}s");
}
