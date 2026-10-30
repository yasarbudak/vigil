using Vigil.Activity;
using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Platform.Linux;
using MacOSStrategy = Vigil.Platform.MacOS.InjectionStrategy;
using WindowsStrategy = Vigil.Platform.Windows.InjectionStrategy;
using LinuxStrategy = Vigil.Platform.Linux.InjectionStrategy;

namespace Vigil.Tests;

public sealed class ConfigReloaderTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly List<ActivityLogOptions> _factoryCalls = [];
    private readonly ReloadableActivityLog _log = new(new NullActivityLog());
    private VigilSettings? _running;
    private PresenceEngine? _engine;

    public void Dispose() => _dir.Dispose();

    private ConfigReloader Create(string? startupConfig = null)
    {
        if (startupConfig is not null)
        {
            _dir.WriteConfig(startupConfig);
        }

        _running = VigilConfiguration.LoadValidated(_dir.Paths.ConfigPath);
        _engine = new PresenceEngine(_running.Vigil, TimeProvider.System, _log);
        return new ConfigReloader(
            _dir.Paths,
            _engine,
            _log,
            options =>
            {
                _factoryCalls.Add(options);
                return options.Enabled ? new RecordingLog(options) : new NullActivityLog();
            },
            _running);
    }

    private sealed class RecordingLog(ActivityLogOptions options) : IActivityLog
    {
        public ActivityLogOptions Options { get; } = options;

        public void Record(ActivityEntry entry)
        {
        }

        public DailyTotals ReadTotals(DateOnly day) => default;

        public void Prune(DateOnly today)
        {
        }
    }

    [Fact]
    public void A_valid_file_is_applied_to_the_engine_the_activity_log_and_the_strategies()
    {
        var reloader = Create();
        _dir.WriteConfig("""
            { "Vigil": { "Threshold": "00:03:00", "PauseOnBattery": true, "ActivityLog": { "Enabled": true, "RetentionDays": 7 } },
              "MacOS": { "Strategy": "F15KeyUp" }, "Windows": { "Strategy": "F15KeyUp" }, "Linux": { "Strategy": "F15KeyUp" } }
            """);

        var result = reloader.Reload();

        Assert.True(result.Ok);
        Assert.Empty(result.RestartRequired);
        Assert.Equal(TimeSpan.FromMinutes(3), _engine!.Options.Threshold);
        Assert.True(_engine.Options.PauseOnBattery);
        Assert.Equal(7, Assert.IsType<RecordingLog>(_log.Current).Options.RetentionDays);
        Assert.Equal(MacOSStrategy.F15KeyUp, _running!.MacOS.Strategy);
        Assert.Equal(WindowsStrategy.F15KeyUp, _running.Windows.Strategy);
        Assert.Equal(LinuxStrategy.F15KeyUp, _running.Linux.Strategy);
    }

    [Fact]
    public void Disabling_the_activity_log_swaps_in_the_empty_one()
    {
        var reloader = Create();
        _dir.WriteConfig("""{ "Vigil": { "ActivityLog": { "Enabled": false } } }""");

        Assert.True(reloader.Reload().Ok);

        Assert.IsType<NullActivityLog>(_log.Current);
    }

    [Theory]
    [InlineData("""{ "Vigil": { "Threshold": "00:00:00" } }""", "Threshold")]
    [InlineData("""{ "Vigil": { "PollInterval": "00:10:00" } }""", "PollInterval")]
    [InlineData("""{ "Vigil": { "Schedule": { "ActiveDays": [] } } }""", "ActiveDays")]
    [InlineData("""{ "Vigil": { "ActivityLog": { "RetentionDays": 0 } } }""", "RetentionDays")]
    [InlineData("""{ "Vigil": { "Threshold": "not a time" } }""", "Threshold")]
    [InlineData("""{ "Vigil": """, "config.json")]
    public void An_invalid_file_changes_nothing_and_the_error_names_the_problem(string badConfig, string expectedInMessage)
    {
        var reloader = Create("""{ "MacOS": { "Strategy": "MouseNudge" } }""");
        var optionsBefore = _engine!.Options;
        var logBefore = _log.Current;
        _dir.WriteConfig(badConfig);

        var result = reloader.Reload();

        Assert.False(result.Ok);
        Assert.Contains(expectedInMessage, result.Error, StringComparison.Ordinal);
        Assert.Same(optionsBefore, _engine.Options);
        Assert.Same(logBefore, _log.Current);
        Assert.Empty(_factoryCalls);
        Assert.Equal(MacOSStrategy.MouseNudge, _running!.MacOS.Strategy);
    }

    [Fact]
    public void An_invalid_file_leaves_the_running_strategies_alone_even_when_only_a_later_key_is_wrong()
    {
        var reloader = Create();
        _dir.WriteConfig("""{ "MacOS": { "Strategy": "F15KeyUp" }, "Vigil": { "Threshold": "00:00:00" } }""");

        Assert.False(reloader.Reload().Ok);

        Assert.Equal(MacOSStrategy.MouseNudge, _running!.MacOS.Strategy);
    }

    [Fact]
    public void A_new_linux_backend_needs_a_restart_and_is_not_applied()
    {
        var reloader = Create("""{ "Linux": { "Backend": "Auto" } }""");
        _dir.WriteConfig("""{ "Linux": { "Backend": "Portal", "Strategy": "F15KeyUp" } }""");

        var result = reloader.Reload();

        Assert.True(result.Ok);
        Assert.Equal(["Linux.Backend"], result.RestartRequired);
        Assert.Equal(InputBackend.Auto, _running!.Linux.Backend);
        Assert.Equal(LinuxStrategy.F15KeyUp, _running.Linux.Strategy); // A strategy change is read at every injection on the other backends.
    }

    [Fact]
    public void On_the_uinput_backend_a_new_strategy_needs_a_restart_because_the_device_is_made_for_one()
    {
        var reloader = Create("""{ "Linux": { "Backend": "Uinput", "Strategy": "MouseNudge" } }""");
        _dir.WriteConfig("""{ "Linux": { "Backend": "Uinput", "Strategy": "F15KeyUp" } }""");

        var result = reloader.Reload();

        Assert.True(result.Ok);
        Assert.Equal(["Linux.Strategy"], result.RestartRequired);
        Assert.Equal(LinuxStrategy.MouseNudge, _running!.Linux.Strategy);
    }

    [Fact]
    public void The_same_backend_and_strategy_need_no_restart()
    {
        var reloader = Create("""{ "Linux": { "Backend": "Uinput", "Strategy": "F15KeyUp" } }""");

        var result = reloader.Reload();

        Assert.True(result.Ok);
        Assert.Empty(result.RestartRequired);
    }

    [Fact]
    public void A_removed_file_means_defaults()
    {
        var reloader = Create("""{ "Vigil": { "Threshold": "00:03:00" } }""");
        File.Delete(_dir.Paths.ConfigPath);

        Assert.True(reloader.Reload().Ok);

        Assert.Equal(TimeSpan.FromMinutes(4), _engine!.Options.Threshold);
    }
}
