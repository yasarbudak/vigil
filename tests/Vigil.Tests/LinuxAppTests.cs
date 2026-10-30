using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Activity;
using Vigil.Commands;
using Vigil.Configuration;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Daemon;
using Vigil.Ipc;
using Vigil.Logging;
using Vigil.Platform;
using Vigil.Platform.Linux;
using Vigil.Platform.Linux.Uinput;
using LinuxStrategy = Vigil.Platform.Linux.InjectionStrategy;

namespace Vigil.Tests;

public class LinuxConfigurationTests
{
    [Fact]
    public void The_Linux_section_defaults_to_the_mouse_nudge_and_automatic_backend()
    {
        using var dir = new TempDataDirectory();

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(LinuxStrategy.MouseNudge, settings.Linux.Strategy);
        Assert.Equal(InputBackend.Auto, settings.Linux.Backend);
    }

    [Theory]
    [InlineData("Auto", InputBackend.Auto)]
    [InlineData("X11", InputBackend.X11)]
    [InlineData("portal", InputBackend.Portal)]
    [InlineData("Uinput", InputBackend.Uinput)]
    public void The_backend_is_bound_case_insensitively(string value, InputBackend expected)
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig($$"""{ "Linux": { "Strategy": "F15KeyUp", "Backend": "{{value}}" } }""");

        var settings = VigilConfiguration.Load(dir.Paths.ConfigPath);

        Assert.Equal(expected, settings.Linux.Backend);
        Assert.Equal(LinuxStrategy.F15KeyUp, settings.Linux.Strategy);
        Assert.Equal(settings.Linux, settings.Platform.Linux);
    }

    [Theory]
    [InlineData("""{ "Linux": { "Backend": "evdev" } }""")]
    [InlineData("""{ "Linux": { "Strategy": "Telepathy" } }""")]
    public void An_unknown_backend_or_strategy_stops_the_daemon_like_any_bad_value(string json)
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig(json);

        var error = Assert.Throws<ConfigurationException>(() => VigilConfiguration.LoadValidated(dir.Paths.ConfigPath));

        Assert.Contains(dir.Paths.ConfigPath, error.Message, StringComparison.Ordinal);
    }
}

public class LinuxPathsTests
{
    [Fact]
    public void The_default_directory_is_vigil_under_XDG_DATA_HOME()
    {
        Assert.Equal("/data/xdg/vigil", VigilPaths.LinuxDefaultDirectory("/home/me", "/data/xdg").Replace('\\', '/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")] // "should be considered invalid and ignored"
    public void Without_a_usable_XDG_DATA_HOME_it_is_home_local_share(string? xdg)
    {
        Assert.Equal("/home/me/.local/share/vigil", VigilPaths.LinuxDefaultDirectory("/home/me", xdg).Replace('\\', '/'));
    }

    [Fact]
    public void The_portal_token_lives_in_the_data_directory()
    {
        Assert.Equal(Path.Combine("/d", "portal-restore-token"), new VigilPaths("/d").PortalTokenPath);
    }

    [Fact]
    public void The_default_socket_path_stays_within_the_limit_for_a_normal_home()
    {
        // sun_path is 108 bytes on Linux; Vigil refuses more than 100.
        var socket = new VigilPaths(VigilPaths.LinuxDefaultDirectory("/home/a-fairly-long-user-name", null)).SocketPath;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(socket) <= 100, socket);
    }
}

public class LinuxLoggingTests
{
    [Fact]
    public void The_journal_is_recognised_by_comparing_JOURNAL_STREAM_with_standard_error()
    {
        Func<string, string?> env = name => name == "JOURNAL_STREAM" ? "8:123456" : null;

        Assert.True(JournalDetection.IsAttached(env, fd => fd == 2 ? "socket:[123456]" : "/dev/null"));
        Assert.True(JournalDetection.IsAttached(env, fd => fd == 1 ? "socket:[123456]" : "/dev/pts/0"));
    }

    [Fact]
    public void A_redirected_output_is_not_the_journal_even_when_the_variable_is_inherited()
    {
        Func<string, string?> env = name => name == "JOURNAL_STREAM" ? "8:123456" : null;

        Assert.False(JournalDetection.IsAttached(env, _ => "/home/me/vigil.log"));
        Assert.False(JournalDetection.IsAttached(env, _ => "socket:[999]"));
        Assert.False(JournalDetection.IsAttached(env, _ => null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("8:")]
    [InlineData("a:b")]
    [InlineData("8:123:456")]
    public void Without_a_well_formed_JOURNAL_STREAM_there_is_no_journal(string? value)
    {
        Assert.False(JournalDetection.IsAttached(name => name == "JOURNAL_STREAM" ? value : null, _ => "socket:[123456]"));
    }

    [Theory]
    [InlineData("socket:[42]", 42UL)]
    [InlineData("socket:[]", null)]
    [InlineData("pipe:[42]", null)]
    [InlineData("socket:[4x]", null)]
    [InlineData(null, null)]
    public void The_inode_is_read_from_the_procfs_link(string? link, ulong? expected)
    {
        Assert.Equal(expected, JournalDetection.ParseSocketInode(link));
    }

    [Fact]
    public void Under_the_journal_the_host_has_no_log_file_provider_and_otherwise_it_has_one()
    {
        using var dir = new TempDataDirectory();
        var platform = new TestPlatform();

        using var journal = DaemonHost.CreateBuilder(dir.Paths, platform.Factory, logToJournal: true).Build();
        using var terminal = DaemonHost.CreateBuilder(dir.Paths, platform.Factory, logToJournal: false).Build();

        Assert.DoesNotContain(journal.Services.GetServices<ILoggerProvider>(), p => p is RotatingFileLoggerProvider);
        Assert.Contains(terminal.Services.GetServices<ILoggerProvider>(), p => p is RotatingFileLoggerProvider);
    }

    [Fact]
    public void A_failure_that_repeats_is_logged_once_and_again_after_it_changes_or_ends()
    {
        var tracker = new RepeatedFailureTracker();

        Assert.False(tracker.Recovered()); // nothing failed yet
        Assert.True(tracker.ShouldLog(new InvalidOperationException("no display")));
        Assert.False(tracker.ShouldLog(new InvalidOperationException("no display")));
        Assert.False(tracker.ShouldLog(new InvalidOperationException("no display")));
        Assert.True(tracker.ShouldLog(new InvalidOperationException("different")));
        Assert.True(tracker.ShouldLog(new IOException("different"))); // another type is another failure
        Assert.True(tracker.Recovered());
        Assert.False(tracker.Recovered()); // only once
        Assert.True(tracker.ShouldLog(new IOException("different"))); // a new run of failures is logged again
    }

    [Fact]
    public async Task A_platform_that_cannot_work_in_this_process_stops_the_daemon_with_its_message()
    {
        using var dir = new TempDataDirectory();
        var fake = new TestPlatform();
        using var host = DaemonHost.CreateBuilder(
            dir.Paths,
            _ => new PlatformServices("test", fake, fake, fake, fake, null, StartupProblem: "no graphical session here"),
            logToJournal: false).Build();

        var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("no graphical session here", error.Message);
    }
}

public class LinuxStatusTests
{
    private static PresenceStatus Status() =>
        new(PresenceState.Watching, null, null, null, null, null, 0, TimeSpan.Zero, new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Status_shows_the_platform_line_only_when_the_platform_has_something_to_say()
    {
        using var without = new StringWriter();
        using var with = new StringWriter();

        StatusFormatter.Write(without, Status());
        StatusFormatter.Write(with, Status(), "Wayland session, idle time from GNOME Mutter, input through desktop portal (RemoteDesktop)");

        Assert.DoesNotContain("Platform:", without.ToString(), StringComparison.Ordinal);
        Assert.Contains("Platform:        Wayland session, idle time from GNOME Mutter", with.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_daemon_hands_the_platform_summary_to_the_status_reply()
    {
        var engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        var fake = new TestPlatform();
        var platform = new PlatformServices("Linux", fake, fake, fake, fake, null, Summary: "X11 session, input through X11 (XTest)");

        var response = new IpcHandler(engine, new FakeLifetime(), platform).Handle(new IpcRequest(IpcCommand.Status));

        Assert.Equal("X11 session, input through X11 (XTest)", response.Platform);
        Assert.Null(new IpcHandler(engine, new FakeLifetime()).Handle(new IpcRequest(IpcCommand.Status)).Platform);
    }

    [Fact]
    public void The_platform_text_survives_the_json_line_protocol()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new IpcResponse(true, Status: Status(), Platform: "X11 session"), VigilJsonContext.Default.IpcResponse);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, VigilJsonContext.Default.IpcResponse)!;

        Assert.Equal("X11 session", back.Platform);
    }

    [Fact]
    public void A_refused_permission_request_prints_the_hint_of_the_platform()
    {
        using var dir = new TempDataDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var fake = new TestPlatform();

        var code = PermissionsCommand.Request(
            dir.Paths,
            output,
            error,
            _ => new PlatformServices("Linux", fake, fake, fake, new Denied(), new Denied(), PermissionNotGrantedHint: () => "The consent dialog was cancelled."));

        Assert.Equal(1, code);
        Assert.Contains("The consent dialog was cancelled.", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class Denied : Vigil.Core.Abstractions.IPermissionProbe, Vigil.Core.Abstractions.IPermissionRequester
    {
        public bool CanPostEvents() => false;

        public bool RequestAccess() => false;
    }
}

[SupportedOSPlatform("linux")]
public class LinuxPlatformCompositionTests
{
    private static LinuxPlatformServices Create(SessionType type, LinuxOptions? options = null, string reason = "test")
    {
        var detection = new SessionDetection(type, reason);
        return LinuxPlatform.Create(options ?? new LinuxOptions(), "/nonexistent/portal-restore-token", detection);
    }

    [Fact]
    public void Without_a_session_the_daemon_must_not_start_and_diag_says_why()
    {
        var services = Create(SessionType.Unknown, reason: "nothing is set");

        Assert.Contains("nothing is set", services.StartupProblem, StringComparison.Ordinal);
        Assert.Contains("systemctl --user import-environment", services.StartupProblem, StringComparison.Ordinal);
        Assert.False(services.Permissions.CanPostEvents());
        Assert.False(services.PermissionRequester.RequestAccess());
        var failure = Assert.Throws<LinuxUnavailableException>(() => services.Idle.GetIdleTime());
        Assert.Contains("nothing is set", failure.Message, StringComparison.Ordinal);
        Assert.False(services.Injector.Inject().Posted);
        Assert.Contains("nothing is set", services.NotGrantedHint(), StringComparison.Ordinal);
        Assert.Contains(services.Diagnose(), d => d.Label == "Session type:" && d.Value.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void An_X11_session_uses_the_X_server_and_XTest()
    {
        var services = Create(SessionType.X11, reason: "DISPLAY=:0");

        Assert.Null(services.StartupProblem);
        Assert.Contains("X11 session", services.Summary, StringComparison.Ordinal);
        Assert.Contains("X server", services.Summary, StringComparison.Ordinal);
        Assert.Contains("X11 (XTest)", services.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Wayland_session_uses_the_GNOME_idle_monitor_and_the_portal_and_asks_for_no_consent_by_itself()
    {
        var services = Create(SessionType.Wayland, reason: "XDG_SESSION_TYPE=wayland");

        Assert.Null(services.StartupProblem);
        Assert.Contains("GNOME Mutter", services.Summary, StringComparison.Ordinal);
        Assert.Contains("desktop portal", services.Summary, StringComparison.Ordinal);
        Assert.False(services.Permissions.CanPostEvents()); // no token file: the probe says not granted and never prompts
        var details = services.Diagnose();
        Assert.Contains(details, d => d is { Label: "Session type:", Value: "Wayland (XDG_SESSION_TYPE=wayland)" });
        Assert.Contains(details, d => d.Label == "Portal consent:" && d.Value.Contains("none saved", StringComparison.Ordinal));
    }

    [Fact]
    public void Uinput_is_used_only_when_the_configuration_selects_it()
    {
        var automatic = Create(SessionType.X11);
        var chosen = Create(SessionType.X11, new LinuxOptions { Backend = InputBackend.Uinput });

        Assert.DoesNotContain("uinput", automatic.Summary, StringComparison.Ordinal);
        Assert.Contains("uinput", chosen.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_portal_can_be_chosen_in_an_X11_session_and_X11_on_Wayland_is_warned_about()
    {
        Assert.Contains("desktop portal", Create(SessionType.X11, new LinuxOptions { Backend = InputBackend.Portal }).Summary, StringComparison.Ordinal);

        var warned = Create(SessionType.Wayland, new LinuxOptions { Backend = InputBackend.X11 });
        Assert.Contains("X11 (XTest)", warned.Summary, StringComparison.Ordinal);
        Assert.Contains(warned.Diagnose().Take(4), d => d.Label == "WARNING:" && d.Value.Contains("XWayland", StringComparison.Ordinal));
    }
}

[SupportedOSPlatform("linux")]
public class LinuxUinputTests
{
    private static Func<string, UinputOpenResult> Opener(UinputOpenResult first, UinputOpenResult second = UinputOpenResult.Missing) =>
        path => path == "/dev/uinput" ? first : second;

    [Fact]
    public void The_device_nodes_are_the_ones_the_kernel_documentation_names()
    {
        Assert.Equal(["/dev/uinput", "/dev/input/uinput"], UinputProtocol.DevicePaths);
    }

    [Fact]
    public void A_writable_node_is_found_and_the_older_location_is_tried_second()
    {
        Assert.Equal("/dev/uinput", UinputAccess.Probe(Opener(UinputOpenResult.Writable), out var problem));
        Assert.Null(problem);
        Assert.Equal("/dev/input/uinput", UinputAccess.Probe(Opener(UinputOpenResult.Missing, UinputOpenResult.Writable), out _));
    }

    [Fact]
    public void A_node_the_user_may_not_write_points_at_the_udev_rule_and_says_root_is_never_asked_for()
    {
        Assert.Null(UinputAccess.Probe(Opener(UinputOpenResult.Denied), out var problem));

        Assert.Contains("may not write", problem, StringComparison.Ordinal);
        Assert.Contains("udev rule", problem, StringComparison.Ordinal);
        Assert.Contains("never asks for root", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void No_node_at_all_means_the_module_is_not_loaded()
    {
        Assert.Null(UinputAccess.Probe(Opener(UinputOpenResult.Missing), out var problem));

        Assert.Contains("modprobe uinput", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backend_reports_access_without_creating_a_device()
    {
        using var granted = new UinputInputBackend(new LinuxOptions(), Opener(UinputOpenResult.Writable));
        using var denied = new UinputInputBackend(new LinuxOptions(), Opener(UinputOpenResult.Denied));

        Assert.True(granted.CanPostEvents());
        Assert.True(granted.RequestAccess());
        Assert.Contains(granted.Diagnose(), d => d is { Label: "uinput:", Value: "/dev/uinput is writable" });
        Assert.False(denied.CanPostEvents());
        Assert.False(denied.RequestAccess());
        Assert.Contains("udev rule", denied.NotGrantedHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Injecting_without_access_fails_with_the_reason_and_creates_nothing()
    {
        using var denied = new UinputInputBackend(new LinuxOptions(), Opener(UinputOpenResult.Denied));

        var result = denied.Inject();

        Assert.False(result.Posted);
        Assert.Contains("may not write", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_opener_classifies_what_opening_for_writing_gives()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /dev/null as a stand-in for a writable device node.");

        Assert.Equal(UinputOpenResult.Writable, UinputAccess.TryOpen("/dev/null"));
        Assert.Equal(UinputOpenResult.Missing, UinputAccess.TryOpen("/nonexistent/uinput"));
        Assert.Equal(UinputOpenResult.Denied, UinputAccess.TryOpen(Path.GetTempPath())); // a directory cannot be opened for writing
    }
}
