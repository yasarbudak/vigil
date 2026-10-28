using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Activity;
using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Daemon;
using Vigil.Ipc;
using Vigil.Platform.MacOS;

namespace Vigil.Tests;

public class DaemonHostTests
{
    private sealed class CollectingLoggerProvider : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public void Dispose()
        {
        }
    }

    private static IHost Build(TempDataDirectory dir, TestPlatform? platform = null) =>
        DaemonHost.CreateBuilder(dir.Paths, (platform ?? new TestPlatform()).Factory).Build();

    [Fact]
    public void Missing_config_file_means_defaults()
    {
        using var dir = new TempDataDirectory();
        using var host = Build(dir);

        var options = host.Services.GetRequiredService<VigilOptions>();

        Assert.Equal(TimeSpan.FromMinutes(4), options.Threshold);
        Assert.IsType<JsonlActivityLog>(host.Services.GetRequiredService<IActivityLog>());
    }

    [Fact]
    public void Config_file_is_bound_including_schedule_and_macOS_strategy()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""
            {
              "Vigil": {
                "Threshold": "00:03:00",
                "PollInterval": "00:00:02",
                "PauseOnBattery": true,
                "Schedule": { "ActiveDays": ["Monday", "Tuesday"], "Start": "09:00", "End": "17:30" }
              },
              "MacOS": { "Strategy": "F15KeyUp" }
            }
            """);
        using var host = Build(dir);

        var options = host.Services.GetRequiredService<VigilOptions>();

        Assert.Equal(TimeSpan.FromMinutes(3), options.Threshold);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.True(options.PauseOnBattery);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], options.Schedule.ActiveDays);
        Assert.Equal(new TimeOnly(9, 0), options.Schedule.Start);
        Assert.Equal(new TimeOnly(17, 30), options.Schedule.End);
        Assert.Equal(InjectionStrategy.F15KeyUp, host.Services.GetRequiredService<MacOSOptions>().Strategy);
    }

    [Fact]
    public async Task Daemon_start_deletes_expired_activity_logs_and_keeps_everything_else()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Vigil": { "ActivityLog": { "RetentionDays": 5 } } }""");
        var expired = Path.Combine(dir.Paths.DataDirectory, "activity-2020-01-01.jsonl");
        var unrelated = Path.Combine(dir.Paths.DataDirectory, "notes.txt");
        var recent = dir.Paths.ActivityLogPath(DateOnly.FromDateTime(DateTime.Now));
        File.WriteAllText(expired, "x");
        File.WriteAllText(unrelated, "x");
        File.WriteAllText(recent, "x");
        using var host = Build(dir);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Disabled_activity_log_writes_nothing()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Vigil": { "ActivityLog": { "Enabled": false } } }""");
        using var host = Build(dir);

        Assert.IsType<NullActivityLog>(host.Services.GetRequiredService<IActivityLog>());
    }

    [Fact]
    public void Invalid_values_stop_the_daemon_before_the_host_is_built()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Vigil": { "Threshold": "00:00:00" } }""");

        var error = Assert.Throws<ConfigurationException>(() => Build(dir));

        Assert.Contains("Threshold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_stops_the_daemon_with_a_clear_message()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("{ bad");

        var error = Assert.Throws<ConfigurationException>(() => Build(dir));

        Assert.Contains(dir.Paths.ConfigPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_daemon_answers_status_pause_and_resume_then_cleans_up()
    {
        using var dir = new TempDataDirectory();
        using var host = Build(dir);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var status = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Status), TestContext.Current.CancellationToken);
        Assert.Equal(PresenceState.Watching, status.Status!.State);

        var paused = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(15)), TestContext.Current.CancellationToken);
        Assert.Equal(PresenceState.Paused, paused.Status!.State);
        Assert.NotNull(paused.Status.PausedUntil);

        var invalid = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(-1)), TestContext.Current.CancellationToken);
        Assert.False(invalid.Ok);

        var resumed = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Resume), TestContext.Current.CancellationToken);
        Assert.Equal(PresenceState.Watching, resumed.Status!.State);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(dir.Paths.SocketPath));
    }

    [Fact]
    public async Task A_second_daemon_fails_to_start_and_neither_injects_nor_writes_the_activity_log()
    {
        using var dir = new TempDataDirectory();
        using var first = Build(dir, new TestPlatform());
        await first.StartAsync(TestContext.Current.CancellationToken);

        // The second daemon's user has been idle for an hour: if its worker ever ran it would inject at once.
        var eager = new TestPlatform { Idle = TimeSpan.FromHours(1) };
        using var second = Build(dir, eager);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken); // Time for a wrongly started worker to act.

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, eager.InjectCalls);
        Assert.Empty(Directory.GetFiles(dir.Paths.DataDirectory, "activity-*.jsonl"));

        // The first daemon is unharmed.
        var status = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Status), TestContext.Current.CancellationToken);
        Assert.True(status.Ok);
        await first.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_warns_when_PauseOnLock_is_enabled_but_lock_state_is_unknown()
    {
        using var dir = new TempDataDirectory();
        var logs = new CollectingLoggerProvider();
        var builder = DaemonHost.CreateBuilder(dir.Paths, new TestPlatform { Locked = null }.Factory);
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        lock (logs.Entries)
        {
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("PauseOnLock", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Startup_does_not_warn_when_lock_state_is_known()
    {
        using var dir = new TempDataDirectory();
        var logs = new CollectingLoggerProvider();
        var builder = DaemonHost.CreateBuilder(dir.Paths, new TestPlatform { Locked = false }.Factory);
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        lock (logs.Entries)
        {
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("PauseOnLock", StringComparison.Ordinal));
        }
    }
}
