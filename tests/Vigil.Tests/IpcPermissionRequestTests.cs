using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Activity;
using Vigil.Commands;
using Vigil.Core.Abstractions;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Ipc;
using Vigil.Platform;
using Vigil.Platform.Linux;
using Vigil.Platform.Linux.Portal;

namespace Vigil.Tests;

/// <summary>
/// <c>vigil permissions request</c> on a platform whose consent belongs to the daemon (Linux portal): the CLI sends
/// <see cref="IpcCommand.RequestPermission"/> over a real socket to a real <see cref="IpcServer"/> and <see cref="IpcHandler"/>, and the
/// daemon runs the flow against a fake portal. No bus, no real daemon, no dialog.
/// </summary>
public sealed class IpcPermissionRequestTests : IDisposable
{
    private static readonly PortalTimeouts Quick = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));

    private readonly TempDataDirectory _dir = new();
    private readonly TestPlatform _fake = new();
    private readonly FakePortalConnection _portal = new();
    private readonly PortalTokenStore _tokens;
    private readonly List<IDisposable> _disposables = [];

    public IpcPermissionRequestTests() => _tokens = new PortalTokenStore(Path.Combine(_dir.Paths.DataDirectory, "portal-restore-token"));

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _dir.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Records that it was asked; the CLI process must never be the one that asks.</summary>
    private sealed class CliSideConsent : IConsentRequester
    {
        public int Calls { get; private set; }

        public Task<ConsentResult> RequestConsentAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ConsentResult(ConsentOutcome.Granted, "asked in the CLI process"));
        }
    }

    private sealed class FakeConsent(Func<CancellationToken, Task<ConsentResult>> run) : IConsentRequester
    {
        public int Calls { get; private set; }

        public Task<ConsentResult> RequestConsentAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return run(cancellationToken);
        }
    }

    private PlatformServices PlatformWith(IConsentRequester? consent) =>
        new("Linux", _fake, _fake, _fake, _fake, null, DaemonConsent: consent);

    private PortalSessionManager NewManager(PortalTimeouts? timeouts = null)
    {
        var manager = new PortalSessionManager(new PortalClient(new FakeConnector(_ => _portal), timeouts ?? Quick), _tokens, new LinuxOptions());
        _disposables.Add(manager);
        return manager;
    }

    private sealed class ListLogger : ILogger<IpcHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

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

        public (LogLevel Level, string Message)[] Snapshot()
        {
            lock (Entries)
            {
                return [.. Entries];
            }
        }
    }

    private readonly ListLogger _log = new();

    private async Task<IpcServer> StartDaemonAsync(PlatformServices? platform, TimeSpan? clientTimeout = null, TimeSpan? consentLimit = null)
    {
        var engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        var handler = new IpcHandler(engine, new FakeLifetime(), platform, consentLimit, _log);
        var server = new IpcServer(_dir.Paths.SocketPath, handler, NullLogger<IpcServer>.Instance, clientTimeout);
        _disposables.Add(server);
        await server.StartAsync(Token);
        return server;
    }

    private async Task<(int Code, string Output, string Error)> RunCliAsync(CancellationToken cancellationToken, CliSideConsent? cli = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var platform = PlatformWith(cli ?? new CliSideConsent());
        var code = await PermissionsCommand.RequestAsync(
            _dir.Paths,
            output,
            error,
            _ => platform,
            cancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_daemon_runs_the_flow_stores_the_token_and_keeps_the_session_for_injection()
    {
        var manager = NewManager();
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(manager)));
        var cli = new CliSideConsent();

        var (code, output, error) = await RunCliAsync(TestContext.Current.CancellationToken, cli);

        Assert.Equal(0, code);
        Assert.Contains("granted", output, StringComparison.Ordinal);
        Assert.Empty(error);
        Assert.Equal(0, cli.Calls); // the CLI process asked nothing
        Assert.Equal("token_after", _tokens.Read());
        Assert.True(manager.Inject().Posted);
        Assert.Single(_portal.CallsTo("Start")); // the session of the request is the one used, no second start
    }

    [Fact]
    public async Task A_cancelled_dialog_exits_1_and_says_so()
    {
        _portal.StartResponse = 1;
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager())));

        var (code, output, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("cancelled", error, StringComparison.Ordinal);
        Assert.DoesNotContain("is granted", output, StringComparison.Ordinal);
        Assert.False(_tokens.Exists());
    }

    [Fact]
    public async Task A_consent_without_the_needed_device_is_reported_as_denied()
    {
        _portal.GrantedDevices = 1; // keyboard only; the default MouseNudge needs the pointer
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager())));

        var (code, _, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("denied", error, StringComparison.Ordinal);
        Assert.Contains("pointer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dialog_nobody_answers_is_reported_as_timed_out()
    {
        _portal.StartSilence = true;
        var shortWait = new PortalTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager(shortWait))));

        var (code, _, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("timed out", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_daemon_ends_a_request_that_waits_longer_than_its_limit_and_still_answers()
    {
        var waiting = new FakeConsent(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ConsentResult(ConsentOutcome.Granted, "unreachable");
        });
        await StartDaemonAsync(PlatformWith(waiting), consentLimit: TimeSpan.FromMilliseconds(200));

        var (code, _, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("timed out", error, StringComparison.Ordinal);
        Assert.Equal(1, waiting.Calls);
    }

    [Fact]
    public async Task A_second_request_while_one_waits_is_refused_as_already_in_progress()
    {
        _portal.StartSilence = true; // the dialog nobody has answered yet
        var server = await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager())));
        var first = RunCliAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _portal.CallsTo("Start").Any());

        var (code, _, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("already in progress", error, StringComparison.Ordinal);
        Assert.Single(_portal.CallsTo("Start"));

        // Stopping the daemon ends the waiting request; its client sees the connection close, not a hang.
        await server.StopAsync(Token);
        var (firstCode, _, firstError) = await first;
        Assert.Equal(1, firstCode);
        Assert.NotEmpty(firstError);
    }

    [Fact]
    public async Task The_servers_short_connection_timeout_does_not_kill_a_request_that_waits_for_the_user()
    {
        var slow = new FakeConsent(async token =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), token);
            return new ConsentResult(ConsentOutcome.Granted, "answered late");
        });
        await StartDaemonAsync(PlatformWith(slow), clientTimeout: TimeSpan.FromMilliseconds(200));

        var (code, output, _) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("answered late", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_daemon_the_cli_says_how_to_start_one_and_asks_nothing_itself()
    {
        var cli = new CliSideConsent();

        var (code, output, error) = await RunCliAsync(TestContext.Current.CancellationToken, cli);

        Assert.Equal(1, code);
        Assert.Contains("vigil install", error, StringComparison.Ordinal);
        Assert.Contains("vigil run", error, StringComparison.Ordinal);
        Assert.Equal(0, cli.Calls); // no fallback to asking from the CLI process
        Assert.DoesNotContain("granted", output, StringComparison.Ordinal);
        Assert.Empty(_portal.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_daemon_whose_platform_has_nothing_to_ask_rejects_the_command_and_runs_no_flow(bool withPlatform)
    {
        // macOS, Windows, Linux with X11 or uinput: no DaemonConsent. The handler without any platform is the same case.
        await StartDaemonAsync(withPlatform ? PlatformWith(consent: null) : null);

        var (code, _, error) = await RunCliAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("no permission to request", error, StringComparison.Ordinal);
        Assert.Contains("refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_permission_request_command_can_start_the_flow()
    {
        var consent = new FakeConsent(_ => Task.FromResult(new ConsentResult(ConsentOutcome.Granted, "ok")));
        var engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        var handler = new IpcHandler(engine, new FakeLifetime(), PlatformWith(consent));

        foreach (var command in Enum.GetValues<IpcCommand>().Where(c => c != IpcCommand.RequestPermission && c != IpcCommand.Pause))
        {
            await handler.HandleAsync(new IpcRequest(command), Token);
        }

        // The quick path does not know the command either: asking is only through HandleAsync.
        Assert.False(handler.Handle(new IpcRequest(IpcCommand.RequestPermission)).Ok);
        Assert.Equal(0, consent.Calls);

        var response = await handler.HandleAsync(new IpcRequest(IpcCommand.RequestPermission), Token);

        Assert.True(response.Ok);
        Assert.Equal(ConsentOutcome.Granted, response.Consent);
        Assert.Equal(1, consent.Calls);
    }

    [Fact]
    public async Task A_daemon_that_is_stopping_does_not_turn_the_cancellation_into_an_answer()
    {
        var consent = new FakeConsent(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ConsentResult(ConsentOutcome.Granted, "unreachable");
        });
        var engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        var handler = new IpcHandler(engine, new FakeLifetime(), PlatformWith(consent));
        using var stopping = new CancellationTokenSource();

        var pending = handler.HandleAsync(new IpcRequest(IpcCommand.RequestPermission), stopping.Token);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_client_that_goes_away_cancels_the_pending_request_closes_the_dialog_and_frees_the_slot()
    {
        _portal.StartSilence = true; // the dialog nobody has answered yet
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager())));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = RunCliAsync(cancel.Token);
        await WaitUntilAsync(() => _portal.CallsTo("Start").Any());

        await cancel.CancelAsync(); // Ctrl+C: the CLI closes its socket

        var (firstCode, _, firstError) = await first;
        Assert.Equal(1, firstCode);
        Assert.Contains("cancelled here", firstError, StringComparison.Ordinal);
        await WaitUntilAsync(() => _portal.CallsTo("Close").Any(c => c.Interface == PortalProtocol.RequestInterface)); // the dialog is closed at the portal

        // The slot is free long before the portal's own 5 minutes: a new request is a normal one.
        _portal.StartSilence = false;
        var second = (Code: -1, Output: "", Error: "");
        await WaitUntilAsync(() =>
        {
            second = RunCliAsync(Token).GetAwaiter().GetResult();
            return !second.Error.Contains("already in progress", StringComparison.Ordinal);
        });
        Assert.Equal(0, second.Code);
        Assert.Contains(_log.Snapshot(), e => e.Level == LogLevel.Information && e.Message.Contains("cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_server_cancels_the_handlers_token_when_the_peer_closes_the_connection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new FakeConsent(async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return new ConsentResult(ConsentOutcome.Granted, "unreachable");
        });
        await StartDaemonAsync(PlatformWith(consent));

        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_dir.Paths.SocketPath), Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"command\":\"RequestPermission\"}\n"), Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(cancelled.Task.IsCompleted); // a client that is still there keeps its request
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    [Fact]
    public async Task The_daemon_logs_the_request_and_its_outcome_without_any_token()
    {
        await StartDaemonAsync(PlatformWith(new PortalInputBackend(NewManager())));
        await RunCliAsync(Token); // granted: the fake portal's token is "token_after"
        _portal.StartResponse = 1;
        await RunCliAsync(Token); // cancelled

        var entries = _log.Snapshot();
        Assert.Equal(2, entries.Count(e => e.Message.Contains("Permission request received", StringComparison.Ordinal)));
        Assert.Contains(entries, e => e.Level == LogLevel.Information && e.Message.Contains("Granted", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.Contains("token_after", StringComparison.Ordinal));
    }

    [Fact]
    public void The_consent_result_survives_the_json_line_protocol()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new IpcResponse(false, "no", Consent: ConsentOutcome.TimedOut), VigilJsonContext.Default.IpcResponse);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, VigilJsonContext.Default.IpcResponse)!;

        Assert.Equal(ConsentOutcome.TimedOut, back.Consent);
        Assert.Contains("\"timedOut\"", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10, Token);
        }
    }
}

public class IpcTimeoutTests
{
    [Fact]
    public void Every_command_but_the_permission_request_keeps_the_short_timeout()
    {
        foreach (var command in Enum.GetValues<IpcCommand>().Where(c => c != IpcCommand.RequestPermission))
        {
            Assert.Equal(TimeSpan.FromSeconds(5), IpcTimeouts.For(command));
        }

        Assert.Equal(TimeSpan.FromMinutes(6), IpcTimeouts.For(IpcCommand.RequestPermission));
    }

    [Fact]
    public void The_daemons_limit_lies_between_the_portals_own_wait_and_the_clients_wait()
    {
        var portal = PortalTimeouts.Default;
        var portalWorstCase = portal.StartInteractive + (2 * portal.Step); // the two quick steps before the dialog, then the dialog

        Assert.True(portalWorstCase < IpcTimeouts.ConsentDaemonLimit, "The portal's own timeout must come first, with its precise message.");
        Assert.True(IpcTimeouts.ConsentDaemonLimit < IpcTimeouts.Consent, "The daemon must answer before the client gives up.");
    }

    [Fact]
    public async Task A_client_gives_up_on_a_daemon_that_does_not_answer_within_its_timeout()
    {
        using var dir = new TempDataDirectory();
        using var server = new IpcServer(dir.Paths.SocketPath, new SlowHandler(), NullLogger<IpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IpcClient.SendAsync(
            dir.Paths.SocketPath,
            new IpcRequest(IpcCommand.Status),
            TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromMilliseconds(200)));

        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class SlowHandler : IIpcHandler
    {
        public IpcResponse Handle(IpcRequest request) => new(true);

        public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return Handle(request);
        }
    }
}
