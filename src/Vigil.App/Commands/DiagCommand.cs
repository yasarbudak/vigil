using System.ComponentModel;
using System.Runtime.InteropServices;
using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Platform;

namespace Vigil.Commands;

/// <summary>Read-only report. Reads the idle counter and silently preflights permission; posts and prompts nothing.</summary>
internal static class DiagCommand
{
    public static int Execute(VigilPaths paths, TextWriter output)
    {
        output.WriteLine($"Platform:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        output.WriteLine($"Config path:     {paths.ConfigPath} ({(File.Exists(paths.ConfigPath) ? "found" : "not found, using defaults")})");

        VigilSettings settings;
        try
        {
            settings = VigilConfiguration.Load(paths.ConfigPath);
        }
        catch (ConfigurationException ex)
        {
            output.WriteLine($"Config valid:    no - {ex.Message}");
            return 2;
        }

        var options = settings.Vigil;
        var errors = settings.Validate();
        output.WriteLine($"Config valid:    {(errors.Count == 0 ? "yes" : "no")}");
        foreach (var error in errors)
        {
            output.WriteLine($"                 - {error}");
        }

        WriteEffectiveConfig(output, settings);

        PlatformServices platform;
        try
        {
            platform = PlatformServices.Create(settings.Platform);
        }
        catch (PlatformNotSupportedException ex)
        {
            output.WriteLine($"Supported:       no - {ex.Message}");
            return 1;
        }

        output.WriteLine($"Idle time:       {DescribeIdleTime(platform.Idle)}");
        output.WriteLine($"Post events:     {(platform.Permissions.CanPostEvents() ? "granted" : "not granted (run 'vigil permissions request')")}");
        output.WriteLine($"Screen locked:   {Describe(platform.Session.IsScreenLocked(), "yes", "no")}");
        output.WriteLine($"On battery:      {Describe(platform.Session.IsOnBattery(), "yes", "no")}");
        if (StartupWarnings.LockDetection(options, platform.Session) is { } warning)
        {
            output.WriteLine($"WARNING:         {warning}");
        }

        return errors.Count == 0 ? 0 : 2;
    }

    private static void WriteEffectiveConfig(TextWriter output, VigilSettings settings)
    {
        var options = settings.Vigil;
        var schedule = options.Schedule;
        var days = schedule.ActiveDays is { } active ? string.Join(",", active) : "every day";

        output.WriteLine($"Threshold:       {options.Threshold}");
        output.WriteLine($"Poll interval:   {options.PollInterval}");
        output.WriteLine($"Verify delay:    {options.VerifyDelay}");
        output.WriteLine($"Retry interval:  {options.DegradedRetryInterval}");
        output.WriteLine($"Pause on lock:   {options.PauseOnLock}");
        output.WriteLine($"Pause on battery: {options.PauseOnBattery}");
        output.WriteLine($"Schedule:        {days}, {schedule.Start:HH:mm} to {schedule.End:HH:mm}");
        output.WriteLine($"Activity log:    {(options.ActivityLog.Enabled ? "enabled" : "disabled")}");
        output.WriteLine($"Log retention:   {options.ActivityLog.RetentionDays} days");
        output.WriteLine(OperatingSystem.IsWindows()
            ? $"Windows strategy: {settings.Windows.Strategy}"
            : $"macOS strategy:  {settings.MacOS.Strategy}");
    }

    /// <summary>The idle counter is the one read that can throw (a failed Win32 call); diag reports it instead of crashing.</summary>
    private static string DescribeIdleTime(IIdleSource idle)
    {
        try
        {
            return $"{idle.GetIdleTime().TotalSeconds:0.0} s";
        }
        catch (Win32Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static string Describe(bool? value, string whenTrue, string whenFalse) =>
        value is { } known ? (known ? whenTrue : whenFalse) : "unknown (could not be determined)";
}
