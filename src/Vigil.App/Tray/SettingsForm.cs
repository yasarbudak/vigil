using System.Globalization;
using Vigil.Configuration;
using Vigil.Core.Options;

namespace Vigil.Tray;

/// <summary>
/// The settings window's fields, as the text and choices a person edits. Values stay text until the daemon's own loader
/// (<see cref="VigilConfiguration"/>) reads them, so a number or time is judged by the same parser and the same rules the
/// daemon uses, and the window holds none of its own.
/// </summary>
/// <param name="ActiveDays">The days a window may start on; all seven means every day.</param>
/// <param name="Strategy">The injection strategy of the OS the tray runs on (<c>MouseNudge</c> or <c>F15KeyUp</c>); the other OS sections are left alone.</param>
internal sealed record SettingsForm(
    string Threshold,
    bool PauseOnLock,
    bool PauseOnBattery,
    IReadOnlyList<DayOfWeek> ActiveDays,
    string ScheduleStart,
    string ScheduleEnd,
    bool ActivityLogEnabled,
    string RetentionDays,
    string Strategy)
{
    public static readonly IReadOnlyList<DayOfWeek> AllDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The values the settings window offers for the strategy; the names are those of the enum in config.json.</summary>
    public static readonly IReadOnlyList<string> Strategies = ["MouseNudge", "F15KeyUp"];

    /// <param name="platformSection">The config section of the running OS: <see cref="PlatformSections"/>.</param>
    public static SettingsForm From(VigilSettings settings, string platformSection)
    {
        var options = settings.Vigil;
        return new SettingsForm(
            options.Threshold.ToString("c", CultureInfo.InvariantCulture),
            options.PauseOnLock,
            options.PauseOnBattery,
            options.Schedule.ActiveDays is { Length: > 0 } days ? [.. AllDays.Where(days.Contains)] : AllDays,
            FormatTime(options.Schedule.Start),
            FormatTime(options.Schedule.End),
            options.ActivityLog.Enabled,
            options.ActivityLog.RetentionDays.ToString(CultureInfo.InvariantCulture),
            PlatformSections.StrategyOf(settings, platformSection));
    }

    /// <summary>"HH:mm" as the documentation writes it, with seconds only when the file has them.</summary>
    private static string FormatTime(TimeOnly time) =>
        time.ToString(time.Second == 0 && time.Millisecond == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>The three platform sections of config.json and the one that has an effect on this OS.</summary>
internal static class PlatformSections
{
    public const string MacOS = "MacOS";
    public const string Windows = "Windows";
    public const string Linux = "Linux";

    /// <summary>The section of the running OS; null on an OS Vigil does not support (the tray then offers no strategy).</summary>
    public static string? ForCurrentOS() =>
        OperatingSystem.IsMacOS() ? MacOS
        : OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsLinux() ? Linux
        : null;

    public static string StrategyOf(VigilSettings settings, string section) => section switch
    {
        MacOS => settings.MacOS.Strategy.ToString(),
        Windows => settings.Windows.Strategy.ToString(),
        Linux => settings.Linux.Strategy.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown platform section."),
    };
}
