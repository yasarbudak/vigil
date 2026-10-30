using System.Text.Json;
using Vigil.Core.Presence;
using Vigil.Ipc;
using Vigil.Tray;

namespace Vigil.Tests;

public class DaemonClientTests
{
    private static readonly PresenceStatus Status =
        new(PresenceState.Watching, null, null, null, null, null, 0, TimeSpan.Zero, new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero));

    private static DaemonClient With(Func<IpcRequest, CancellationToken, Task<IpcResponse>> transport) => new(transport);

    [Fact]
    public async Task An_answer_carries_the_status_the_platform_and_the_restart_list()
    {
        var client = With((_, _) => Task.FromResult(new IpcResponse(true, Status: Status, Platform: "p", RestartRequired: ["Linux.Backend"])));

        var reply = await client.SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.True(reply.Reachable);
        Assert.True(reply.Ok);
        Assert.Same(Status, reply.Status);
        Assert.Equal("p", reply.Platform);
        Assert.Equal(["Linux.Backend"], reply.RestartRequired);
    }

    [Fact]
    public async Task The_request_is_passed_on_unchanged()
    {
        IpcRequest? seen = null;
        var client = With((request, _) =>
        {
            seen = request;
            return Task.FromResult(new IpcResponse(true, Status: Status));
        });

        await client.SendAsync(TrayCommands.Pause(PauseChoice.OneHour), TestContext.Current.CancellationToken);

        Assert.Equal(TrayCommands.Pause(PauseChoice.OneHour), seen);
    }

    [Fact]
    public async Task A_refusal_is_reachable_but_not_ok_and_keeps_the_daemons_words()
    {
        var client = With((_, _) => Task.FromResult(new IpcResponse(false, "Invalid configuration in x: Threshold must be greater than zero.")));

        var reply = await client.SendAsync(TrayCommands.Reload(), TestContext.Current.CancellationToken);

        Assert.True(reply.Reachable);
        Assert.False(reply.Ok);
        Assert.Equal("Invalid configuration in x: Threshold must be greater than zero.", reply.Error);
    }

    [Fact]
    public async Task No_daemon_is_not_reachable()
    {
        var client = With((_, _) => throw new DaemonNotRunningException("/x/vigil.sock"));

        var reply = await client.SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Contains("not running", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_connection_is_not_reachable()
    {
        var client = With((_, _) => throw new IOException("Broken pipe"));

        var reply = await client.SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Equal("Broken pipe", reply.Error);
    }

    [Fact]
    public async Task An_answer_that_cannot_be_read_is_not_reachable()
    {
        var client = With((_, _) => throw new JsonException("bad"));

        var reply = await client.SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Contains("cannot be read", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_clients_own_time_limit_is_not_reachable()
    {
        var client = With((_, _) => throw new OperationCanceledException());

        var reply = await client.SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Contains("did not answer", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_passed_on()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var client = With((_, token) => throw new OperationCanceledException(token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(TrayCommands.Status(), source.Token));
    }

    [Fact]
    public async Task A_real_daemon_socket_that_does_not_exist_gives_not_running()
    {
        using var dir = new TempDataDirectory();

        var reply = await DaemonClient.ForSocket(dir.Paths.SocketPath).SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Contains("not running", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_socket_path_longer_than_the_daemon_accepts_is_an_error_not_a_crash()
    {
        var tooLong = Path.Combine(Path.GetTempPath(), new string('x', 120), "vigil.sock");

        await Assert.ThrowsAsync<IOException>(() => IpcClient.SendAsync(tooLong, TrayCommands.Status(), TestContext.Current.CancellationToken));
        var reply = await DaemonClient.ForSocket(tooLong).SendAsync(TrayCommands.Status(), TestContext.Current.CancellationToken);

        Assert.False(reply.Reachable);
        Assert.Contains("too long", reply.Error, StringComparison.Ordinal);
    }
}
