using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Presence;
using Vigil.Install;
using Vigil.Ipc;

namespace Vigil.Tests;

/// <summary>The stop request <c>vigil uninstall</c> sends on Windows, over a real server and socket with a fake handler.</summary>
public class IpcShutdownTests
{
    private sealed class StoppingHandler : IIpcHandler
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Succeed { get; set; } = true;

        public Func<Task>? OnShutdown { get; set; }

        public List<IpcCommand> Sent { get; } = [];

        public Task ResponseSentSignal => _sent.Task;

        public IpcResponse Handle(IpcRequest request) =>
            Succeed
                ? new IpcResponse(true, Status: new PresenceStatus(
                    PresenceState.Watching, null, null, null, null, null, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch))
                : new IpcResponse(false, "no");

        public void ResponseSent(IpcRequest request)
        {
            lock (Sent)
            {
                Sent.Add(request.Command);
            }

            if (request.Command == IpcCommand.Shutdown)
            {
                _sent.TrySetResult();
                if (OnShutdown is { } stop)
                {
                    _ = Task.Run(stop);
                }
            }
        }
    }

    private static IpcServer CreateServer(VigilPaths paths, IIpcHandler handler) =>
        new(paths.SocketPath, handler, NullLogger<IpcServer>.Instance);

    [Fact]
    public async Task The_server_reports_a_response_as_sent_only_after_writing_it_and_only_when_it_was_ok()
    {
        using var dir = new TempDataDirectory();
        var handler = new StoppingHandler();
        using var server = CreateServer(dir.Paths, handler);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var ok = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Status), TestContext.Current.CancellationToken);
        handler.Succeed = false;
        var failed = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Resume), TestContext.Current.CancellationToken);
        await server.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(ok.Ok);
        Assert.False(failed.Ok);
        Assert.Equal([IpcCommand.Status], handler.Sent);
    }

    [Fact]
    public void Stopping_when_no_daemon_runs_reports_not_running()
    {
        using var dir = new TempDataDirectory();

        var result = new IpcDaemonStopper(dir.Paths.SocketPath).Stop();

        Assert.Equal(DaemonStopOutcome.NotRunning, result.Outcome);
    }

    [Fact]
    public async Task Stopping_asks_the_daemon_over_the_socket_and_returns_when_it_is_gone()
    {
        using var dir = new TempDataDirectory();
        var handler = new StoppingHandler();
        using var server = CreateServer(dir.Paths, handler);
        handler.OnShutdown = () => server.StopAsync(CancellationToken.None);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var result = await Task.Run(() => new IpcDaemonStopper(dir.Paths.SocketPath).Stop(), TestContext.Current.CancellationToken);

        Assert.Equal(DaemonStopOutcome.Stopped, result.Outcome);
        Assert.Equal([IpcCommand.Shutdown], handler.Sent);
        Assert.False(File.Exists(dir.Paths.SocketPath));
    }

    [Fact]
    public async Task A_daemon_that_does_not_go_away_is_reported_as_failed_after_the_time_limit()
    {
        using var dir = new TempDataDirectory();
        var handler = new StoppingHandler(); // Acknowledges the request but never stops.
        using var server = CreateServer(dir.Paths, handler);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var result = await Task.Run(
            () => new IpcDaemonStopper(dir.Paths.SocketPath, TimeSpan.FromMilliseconds(600)).Stop(),
            TestContext.Current.CancellationToken);

        Assert.Equal(DaemonStopOutcome.Failed, result.Outcome);
        Assert.Contains("still answered", result.Detail, StringComparison.Ordinal);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_daemon_that_refuses_is_reported_as_failed_with_its_reason()
    {
        using var dir = new TempDataDirectory();
        var handler = new StoppingHandler { Succeed = false };
        using var server = CreateServer(dir.Paths, handler);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var result = await Task.Run(() => new IpcDaemonStopper(dir.Paths.SocketPath).Stop(), TestContext.Current.CancellationToken);

        Assert.Equal(DaemonStopOutcome.Failed, result.Outcome);
        Assert.Equal("no", result.Detail);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }
}
