using Vigil.Commands;
using Vigil.Configuration;
using Vigil.Platform.MacOS;
using Vigil.Platform.Windows;
using MacOSStrategy = Vigil.Platform.MacOS.InjectionStrategy;
using WindowsStrategy = Vigil.Platform.Windows.InjectionStrategy;

namespace Vigil.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDataDirectory();

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(TimeSpan.FromMinutes(4), settings.Vigil.Threshold);
        Assert.Equal(MacOSStrategy.MouseNudge, settings.MacOS.Strategy);
        Assert.Equal(WindowsStrategy.MouseNudge, settings.Windows.Strategy);
    }

    [Fact]
    public void Non_default_values_of_every_kind_are_bound()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""
            { "Vigil": { "Threshold": "00:03:30", "Schedule": { "Start": "08:15", "End": "19:45", "ActiveDays": ["Friday"] } },
              "MacOS": { "Strategy": "F15KeyUp" },
              "Windows": { "Strategy": "F15KeyUp" } }
            """);

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(TimeSpan.FromSeconds(210), settings.Vigil.Threshold);
        Assert.Equal(new TimeOnly(8, 15), settings.Vigil.Schedule.Start);
        Assert.Equal(new TimeOnly(19, 45), settings.Vigil.Schedule.End);
        Assert.Equal([DayOfWeek.Friday], settings.Vigil.Schedule.ActiveDays);
        Assert.Equal(MacOSStrategy.F15KeyUp, settings.MacOS.Strategy);
        Assert.Equal(WindowsStrategy.F15KeyUp, settings.Windows.Strategy);
    }

    [Fact]
    public void Retention_days_default_to_30_and_are_bound_from_the_file()
    {
        using var dir = new TempDataDirectory();
        Assert.Equal(30, VigilConfiguration.Load(dir.Paths.ConfigPath).Vigil.ActivityLog.RetentionDays);

        dir.WriteConfig("""{ "Vigil": { "ActivityLog": { "RetentionDays": 7 } } }""");

        Assert.Equal(7, VigilConfiguration.Load(dir.Paths.ConfigPath).Vigil.ActivityLog.RetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public void LoadValidated_rejects_retention_days_outside_1_to_3650(int days)
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig($$"""{ "Vigil": { "ActivityLog": { "RetentionDays": {{days}} } } }""");

        var error = Assert.Throws<ConfigurationException>(() => VigilConfiguration.LoadValidated(dir.Paths.ConfigPath));

        Assert.Contains("RetentionDays", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ bad")]
    [InlineData("""{ "Vigil": { "Threshold": "not-a-time" } }""")]
    [InlineData("""{ "MacOS": { "Strategy": "Telepathy" } }""")]
    [InlineData("""{ "Windows": { "Strategy": "Telepathy" } }""")]
    public void Unusable_files_raise_ConfigurationException_naming_the_file(string json)
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig(json);

        var error = Assert.Throws<ConfigurationException>(() => VigilConfiguration.Load(dir.Paths.ConfigPath));

        Assert.Contains(dir.Paths.ConfigPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Windows_section_is_bound_independently_of_the_macOS_one()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Windows": { "Strategy": "F15KeyUp" } }""");

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(WindowsStrategy.F15KeyUp, settings.Windows.Strategy);
        Assert.Equal(MacOSStrategy.MouseNudge, settings.MacOS.Strategy);
        Assert.Equal(settings.Windows, settings.Platform.Windows);
        Assert.Equal(settings.MacOS, settings.Platform.MacOS);
    }

    [Fact]
    public void LoadValidated_rejects_invalid_values()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Vigil": { "PollInterval": "00:10:00" } }""");

        Assert.Throws<ConfigurationException>(() => VigilConfiguration.LoadValidated(dir.Paths.ConfigPath));
    }

    [Fact]
    public void Diag_reports_a_malformed_config_without_a_stack_trace_and_exits_2()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("{ bad");
        var output = new StringWriter();

        var exitCode = DiagCommand.Execute(dir.Paths, output);

        Assert.Equal(2, exitCode);
        Assert.Contains("Config valid:    no", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", output.ToString(), StringComparison.Ordinal);
    }
}
