using Vigil.Commands;
using Vigil.Configuration;
using Vigil.Platform.MacOS;

namespace Vigil.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDataDirectory();

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(TimeSpan.FromMinutes(4), settings.Vigil.Threshold);
        Assert.Equal(InjectionStrategy.MouseNudge, settings.MacOS.Strategy);
    }

    [Fact]
    public void Non_default_values_of_every_kind_are_bound()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""
            { "Vigil": { "Threshold": "00:03:30", "Schedule": { "Start": "08:15", "End": "19:45", "ActiveDays": ["Friday"] } },
              "MacOS": { "Strategy": "F15KeyUp" } }
            """);

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(TimeSpan.FromSeconds(210), settings.Vigil.Threshold);
        Assert.Equal(new TimeOnly(8, 15), settings.Vigil.Schedule.Start);
        Assert.Equal(new TimeOnly(19, 45), settings.Vigil.Schedule.End);
        Assert.Equal([DayOfWeek.Friday], settings.Vigil.Schedule.ActiveDays);
        Assert.Equal(InjectionStrategy.F15KeyUp, settings.MacOS.Strategy);
    }

    [Theory]
    [InlineData("{ bad")]
    [InlineData("""{ "Vigil": { "Threshold": "not-a-time" } }""")]
    [InlineData("""{ "MacOS": { "Strategy": "Telepathy" } }""")]
    public void Unusable_files_raise_ConfigurationException_naming_the_file(string json)
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig(json);

        var error = Assert.Throws<ConfigurationException>(() => VigilConfiguration.Load(dir.Paths.ConfigPath));

        Assert.Contains(dir.Paths.ConfigPath, error.Message, StringComparison.Ordinal);
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
