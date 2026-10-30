using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Vigil.Configuration;
using Vigil.Core.Options;
using Vigil.Tray;

namespace Vigil.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsStore Store(string section = PlatformSections.MacOS) => new(_dir.Paths, section);

    private SettingsForm Defaults(string section = PlatformSections.MacOS) => Store(section).Load().Form;

    private JsonObject SavedFile() => (JsonObject)JsonNode.Parse(File.ReadAllText(_dir.Paths.ConfigPath))!;

    [Fact]
    public void A_missing_file_loads_the_defaults_without_problems()
    {
        var load = Store().Load();

        Assert.Empty(load.Problems);
        Assert.Equal("00:04:00", load.Form.Threshold);
        Assert.True(load.Form.PauseOnLock);
        Assert.False(load.Form.PauseOnBattery);
        Assert.Equal(SettingsForm.AllDays, load.Form.ActiveDays);
        Assert.Equal("00:00", load.Form.ScheduleStart);
        Assert.Equal("00:00", load.Form.ScheduleEnd);
        Assert.True(load.Form.ActivityLogEnabled);
        Assert.Equal("30", load.Form.RetentionDays);
        Assert.Equal("MouseNudge", load.Form.Strategy);
        Assert.False(File.Exists(_dir.Paths.ConfigPath)); // Loading never creates the file.
    }

    [Fact]
    public void The_file_is_shown_as_it_is_with_the_strategy_of_the_chosen_section()
    {
        _dir.WriteConfig("""
            { "Vigil": { "Threshold": "00:03:30", "PauseOnBattery": true,
                         "Schedule": { "ActiveDays": ["Friday", "Monday"], "Start": "08:15", "End": "19:45:30" },
                         "ActivityLog": { "Enabled": false, "RetentionDays": 7 } },
              "MacOS": { "Strategy": "F15KeyUp" }, "Linux": { "Strategy": "MouseNudge" } }
            """);

        var form = Store(PlatformSections.MacOS).Load().Form;

        Assert.Equal("00:03:30", form.Threshold);
        Assert.True(form.PauseOnBattery);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], form.ActiveDays);
        Assert.Equal("08:15", form.ScheduleStart);
        Assert.Equal("19:45:30", form.ScheduleEnd);
        Assert.False(form.ActivityLogEnabled);
        Assert.Equal("7", form.RetentionDays);
        Assert.Equal("F15KeyUp", form.Strategy);
        Assert.Equal("MouseNudge", Store(PlatformSections.Linux).Load().Form.Strategy);
    }

    [Fact]
    public void A_file_with_rule_violations_still_loads_and_reports_the_rules_own_messages()
    {
        _dir.WriteConfig("""{ "Vigil": { "Threshold": "00:00:00" } }""");

        var load = Store().Load();

        Assert.Equal("00:00:00", load.Form.Threshold);
        Assert.Contains("Threshold must be greater than zero.", load.Problems);
    }

    [Fact]
    public void A_file_that_cannot_be_read_loads_the_defaults_and_the_reason()
    {
        _dir.WriteConfig("""{ "Vigil": """);

        var load = Store().Load();

        Assert.Equal("00:04:00", load.Form.Threshold);
        var problem = Assert.Single(load.Problems);
        Assert.Contains(_dir.Paths.ConfigPath, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_creates_the_file_and_the_daemons_loader_reads_the_same_values_back()
    {
        var form = Defaults() with
        {
            Threshold = "00:03:00",
            PauseOnLock = false,
            PauseOnBattery = true,
            ActiveDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            ScheduleStart = "09:00",
            ScheduleEnd = "18:00",
            ActivityLogEnabled = false,
            RetentionDays = "14",
            Strategy = "F15KeyUp",
        };

        var result = Store().Save(form);

        Assert.True(result.Saved);
        var settings = VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath);
        Assert.Equal(TimeSpan.FromMinutes(3), settings.Vigil.Threshold);
        Assert.False(settings.Vigil.PauseOnLock);
        Assert.True(settings.Vigil.PauseOnBattery);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], settings.Vigil.Schedule.ActiveDays);
        Assert.Equal(new TimeOnly(9, 0), settings.Vigil.Schedule.Start);
        Assert.Equal(new TimeOnly(18, 0), settings.Vigil.Schedule.End);
        Assert.False(settings.Vigil.ActivityLog.Enabled);
        Assert.Equal(14, settings.Vigil.ActivityLog.RetentionDays);
        Assert.Equal(Vigil.Platform.MacOS.InjectionStrategy.F15KeyUp, settings.MacOS.Strategy);
        var shown = Store().Load().Form; // The window shows after a save what it saved.
        Assert.Equal(form.ActiveDays, shown.ActiveDays);
        Assert.Equal(form with { ActiveDays = shown.ActiveDays }, shown);
    }

    [Fact]
    public void The_file_uses_the_notation_of_the_documentation()
    {
        Store().Save(Defaults() with { RetentionDays = "14", ActiveDays = [DayOfWeek.Friday] });

        var vigil = (JsonObject)SavedFile()["Vigil"]!;
        Assert.Equal("00:04:00", (string?)vigil["Threshold"]);
        Assert.Equal(14, (int?)vigil["ActivityLog"]!["RetentionDays"]); // A number, not the text typed.
        Assert.Equal(["Friday"], vigil["Schedule"]!["ActiveDays"]!.AsArray().Select(day => (string?)day));
        Assert.Equal("MouseNudge", (string?)SavedFile()["MacOS"]!["Strategy"]);
    }

    [Fact]
    public void Every_day_is_written_as_no_key_the_documented_way()
    {
        _dir.WriteConfig("""{ "Vigil": { "Schedule": { "ActiveDays": ["Friday"] } } }""");

        Store().Save(Defaults() with { ActiveDays = SettingsForm.AllDays });

        Assert.Null(SavedFile()["Vigil"]!["Schedule"]!["ActiveDays"]);
        Assert.Null(VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath).Vigil.Schedule.ActiveDays);
    }

    [Fact]
    public void Keys_the_window_does_not_own_are_kept_as_they_were()
    {
        _dir.WriteConfig("""
            {
              "Vigil": { "PollInterval": "00:00:02", "VerifyDelay": "00:00:00.250", "DegradedRetryInterval": "00:02:00", "Threshold": "00:03:00",
                         "Future": { "Nested": [1, 2, { "x": true }] } },
              "Linux": { "Backend": "Portal", "Strategy": "MouseNudge", "Other": "kept" },
              "Windows": { "Strategy": "F15KeyUp" },
              "SomethingElse": 42
            }
            """);

        var result = Store(PlatformSections.Linux).Save(Defaults(PlatformSections.Linux) with { Threshold = "00:03:30", Strategy = "F15KeyUp" });

        Assert.True(result.Saved);
        var file = SavedFile();
        Assert.Equal("00:00:02", (string?)file["Vigil"]!["PollInterval"]);
        Assert.Equal("00:00:00.250", (string?)file["Vigil"]!["VerifyDelay"]);
        Assert.Equal("00:02:00", (string?)file["Vigil"]!["DegradedRetryInterval"]);
        Assert.Equal(true, (bool?)file["Vigil"]!["Future"]!["Nested"]![2]!["x"]);
        Assert.Equal("Portal", (string?)file["Linux"]!["Backend"]);
        Assert.Equal("kept", (string?)file["Linux"]!["Other"]);
        Assert.Equal("F15KeyUp", (string?)file["Windows"]!["Strategy"]); // Another OS's section is not touched.
        Assert.Equal(42, (int?)file["SomethingElse"]);
        Assert.Equal("00:03:30", (string?)file["Vigil"]!["Threshold"]);
        Assert.Equal("F15KeyUp", (string?)file["Linux"]!["Strategy"]);
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case_so_a_section_is_never_duplicated()
    {
        _dir.WriteConfig("""{ "vigil": { "threshold": "00:03:00", "schedule": { "start": "08:00" } }, "macos": { "strategy": "F15KeyUp" } }""");

        var result = Store().Save(Defaults() with { Threshold = "00:05:00", ScheduleStart = "09:30", ScheduleEnd = "17:00", Strategy = "MouseNudge" });

        Assert.True(result.Saved);
        var file = SavedFile();
        Assert.Equal(["vigil", "macos"], file.Select(pair => pair.Key));
        Assert.Equal("00:05:00", (string?)file["vigil"]!["threshold"]);
        Assert.Equal("09:30", (string?)file["vigil"]!["schedule"]!["start"]);
        Assert.Equal("MouseNudge", (string?)file["macos"]!["strategy"]);
        Assert.Equal(TimeSpan.FromMinutes(5), VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath).Vigil.Threshold); // Duplicate keys would have failed this.
    }

    [Fact]
    public void Comments_and_trailing_commas_the_loader_accepts_do_not_block_a_save()
    {
        _dir.WriteConfig("""
            {
              // my note
              "Vigil": { "Threshold": "00:03:00", },
            }
            """);

        var result = Store().Save(Defaults());

        Assert.True(result.Saved);
        Assert.DoesNotContain("my note", File.ReadAllText(_dir.Paths.ConfigPath), StringComparison.Ordinal);
        Assert.True(VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath).Validate().Count == 0);
    }

    [Theory]
    [InlineData("Threshold", "00:00:00", "Threshold must be greater than zero.")]
    [InlineData("Threshold", "-00:01:00", "Threshold must be greater than zero.")]
    [InlineData("Threshold", "00:00:03", "PollInterval must be shorter than Threshold.")]
    [InlineData("RetentionDays", "0", "ActivityLog.RetentionDays must be between 1 and 3650.")]
    [InlineData("RetentionDays", "3651", "ActivityLog.RetentionDays must be between 1 and 3650.")]
    public void The_rules_are_the_daemons_and_a_refused_form_writes_nothing(string field, string value, string expectedRule)
    {
        var original = """{ "Vigil": { "Threshold": "00:03:00" } }""";
        _dir.WriteConfig(original);
        var form = Defaults();
        form = field == "Threshold" ? form with { Threshold = value } : form with { RetentionDays = value };

        var result = Store().Save(form);

        Assert.False(result.Saved);
        Assert.Contains(expectedRule, Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(_dir.Paths.ConfigPath));
    }

    [Fact]
    public void The_messages_are_exactly_what_the_daemon_prints_for_the_same_file()
    {
        var form = Defaults() with { Threshold = "00:00:00" };
        var result = Store().Save(form);

        var daemon = Assert.Throws<ConfigurationException>(() => VigilConfiguration.LoadFromJsonValidated("""{ "Vigil": { "Threshold": "00:00:00" } }""", _dir.Paths.ConfigPath));

        Assert.Contains(new VigilOptions { Threshold = TimeSpan.Zero }.Validate()[0], daemon.Message, StringComparison.Ordinal);
        Assert.Contains(new VigilOptions { Threshold = TimeSpan.Zero }.Validate()[0], result.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void No_days_chosen_is_refused_with_the_rules_message()
    {
        var result = Store().Save(Defaults() with { ActiveDays = [] });

        Assert.False(result.Saved);
        Assert.Contains("ActiveDays must not be empty", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3:4:5")]
    public void Text_the_loader_cannot_read_as_a_time_is_refused_and_nothing_is_written(string threshold)
    {
        var result = Store().Save(Defaults() with { Threshold = threshold });

        Assert.False(result.Saved);
        var message = Assert.Single(result.Errors);
        Assert.Contains("Vigil:Threshold", message, StringComparison.Ordinal); // The key, so the mistake can be found.
        Assert.Contains("TimeSpan", message, StringComparison.Ordinal);
        Assert.False(File.Exists(_dir.Paths.ConfigPath));
    }

    [Fact]
    public void A_blank_threshold_means_the_default_as_it_does_in_the_file()
    {
        Assert.True(Store().Save(Defaults() with { Threshold = string.Empty }).Saved);

        Assert.Equal("00:04:00", (string?)SavedFile()["Vigil"]!["Threshold"]);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("nine")]
    public void Text_the_loader_cannot_read_as_a_time_of_day_is_refused(string start)
    {
        var result = Store().Save(Defaults() with { ScheduleStart = start });

        Assert.False(result.Saved);
        Assert.Contains("Schedule:Start", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_a_number_of_days_is_refused()
    {
        var result = Store().Save(Defaults() with { RetentionDays = "many" });

        Assert.False(result.Saved);
        Assert.Contains("ActivityLog:RetentionDays", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.False(File.Exists(_dir.Paths.ConfigPath));
    }

    [Fact]
    public void An_unknown_strategy_is_refused()
    {
        var result = Store().Save(Defaults() with { Strategy = "Shake" });

        Assert.False(result.Saved);
        Assert.Contains("MacOS:Strategy", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_value_in_a_key_the_window_does_not_own_is_reported_and_not_hidden()
    {
        _dir.WriteConfig("""{ "Vigil": { "PollInterval": "00:10:00" } }""");

        var result = Store().Save(Defaults());

        Assert.False(result.Saved);
        Assert.Contains("PollInterval must be shorter than Threshold.", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "Vigil": """)]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    public void A_file_that_is_not_a_json_object_is_never_overwritten(string content)
    {
        _dir.WriteConfig(content);

        var result = Store().Save(Defaults());

        Assert.False(result.Saved);
        Assert.Contains("Fix or remove it first", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(_dir.Paths.ConfigPath));
    }

    [Fact]
    public void An_empty_file_is_treated_as_an_empty_object()
    {
        _dir.WriteConfig(string.Empty);

        Assert.True(Store().Save(Defaults()).Saved);
    }

    [UnixOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public void The_file_is_private_to_the_user_even_when_it_was_more_open_before()
    {
        _dir.WriteConfig("{}");
        File.SetUnixFileMode(_dir.Paths.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.True(Store().Save(Defaults()).Saved);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dir.Paths.ConfigPath));
    }

    [UnixOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public void A_new_file_is_0600_whatever_the_umask()
    {
        Assert.True(Store().Save(Defaults()).Saved);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dir.Paths.ConfigPath));
    }

    [Fact]
    public void The_write_is_a_rename_of_a_finished_file_and_leaves_no_temporary_file()
    {
        _dir.WriteConfig("""{ "Vigil": { "Threshold": "00:03:00" } }""");

        Assert.True(Store().Save(Defaults() with { Threshold = "00:06:00" }).Saved);

        Assert.Equal([_dir.Paths.ConfigPath], Directory.GetFiles(_dir.Paths.DataDirectory));
        Assert.Equal("00:06:00", (string?)SavedFile()["Vigil"]!["Threshold"]);
    }

    [Fact]
    public async Task A_reader_polling_during_saves_never_sees_a_half_written_file()
    {
        _dir.WriteConfig("{}");
        var store = Store();
        var form = Defaults();
        using var stop = new CancellationTokenSource();
        var broken = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath);
                }
                catch (ConfigurationException)
                {
                    Interlocked.Increment(ref broken);
                }
            }
        }, CancellationToken.None);

        for (var i = 0; i < 200; i++)
        {
            Assert.True(store.Save(form with { Threshold = i % 2 == 0 ? "00:03:00" : "00:05:00" }).Saved);
        }

        stop.Cancel();
        await reader;
        Assert.Equal(0, broken);
    }

    [Fact]
    public void The_data_directory_is_created_when_a_save_needs_it()
    {
        var missing = new VigilPaths(Path.Combine(_dir.Paths.DataDirectory, "nested"));

        Assert.True(new SettingsStore(missing, PlatformSections.MacOS).Save(Defaults()).Saved);

        Assert.True(File.Exists(missing.ConfigPath));
    }

    [Fact]
    public void The_chosen_platform_section_is_the_one_written()
    {
        Assert.True(Store(PlatformSections.Windows).Save(Defaults(PlatformSections.Windows) with { Strategy = "F15KeyUp" }).Saved);

        var file = SavedFile();
        Assert.Equal("F15KeyUp", (string?)file["Windows"]!["Strategy"]);
        Assert.Null(file["MacOS"]);
        Assert.Null(file["Linux"]);
    }

    [Fact]
    public void An_unknown_platform_section_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsStore(_dir.Paths, "BeOS").Load());
    }
}
