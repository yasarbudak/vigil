using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Presence;
using Vigil.Ipc;

namespace Vigil.Tests;

public class IpcServerTests
{
    private sealed class EchoHandler : IIpcHandler
    {
        public Func<IpcRequest, IpcResponse>? Override { get; set; }

        public IpcResponse Handle(IpcRequest request) =>
            Override?.Invoke(request) ??
            new(true, Error: request.Command.ToString(), Status: new PresenceStatus(
                PresenceState.Watching, null, null, null, null, null, 3, TimeSpan.FromMinutes(12), DateTimeOffset.UnixEpoch));
    }

    private static IpcServer CreateServer(VigilPaths paths, IIpcHandler? handler = null, TimeSpan? clientTimeout = null) =>
        new(paths.SocketPath, handler ?? new EchoHandler(), NullLogger<IpcServer>.Instance, clientTimeout);

    private static async Task<Socket> ConnectRawAsync(VigilPaths paths)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.SocketPath), TestContext.Current.CancellationToken);
        return socket;
    }

    private static Task<IpcResponse> StatusAsync(VigilPaths paths) =>
        IpcClient.SendAsync(paths.SocketPath, new IpcRequest(IpcCommand.Status), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Request_and_response_round_trip_over_the_socket()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var response = await IpcClient.SendAsync(dir.Paths.SocketPath, new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(15)), TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal("Pause", response.Error);
        Assert.Equal(3, response.Status!.InjectionsToday);
        Assert.Equal(TimeSpan.FromMinutes(12), response.Status.CoveredToday);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Socket_is_owner_only_and_removed_on_shutdown()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dir.Paths.SocketPath));
        }

        await server.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(dir.Paths.SocketPath));
    }

    [Fact]
    public async Task The_server_itself_makes_its_directory_owner_only_before_binding()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDataDirectory();
        File.SetUnixFileMode(dir.Paths.DataDirectory, (UnixFileMode)0b111_101_101); // 0755, as a careless caller might leave it
        using var server = CreateServer(dir.Paths);

        await server.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(dir.Paths.DataDirectory));
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stale_socket_file_is_replaced_by_the_lock_holder()
    {
        using var dir = new TempDataDirectory();
        using var crashedDaemon = LeaveStaleSocket(dir.Paths.SocketPath);
        Assert.True(File.Exists(dir.Paths.SocketPath));
        using var server = CreateServer(dir.Paths);

        await server.StartAsync(TestContext.Current.CancellationToken);

        Assert.True((await StatusAsync(dir.Paths)).Ok);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_second_server_fails_without_touching_the_first_ones_socket()
    {
        using var dir = new TempDataDirectory();
        using var first = CreateServer(dir.Paths);
        await first.StartAsync(TestContext.Current.CancellationToken);
        using var second = CreateServer(dir.Paths);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
        Assert.True((await StatusAsync(dir.Paths)).Ok); // The first server's socket is intact and still served.
        await first.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_instance_lock_is_released_on_stop_so_a_new_daemon_can_start()
    {
        using var dir = new TempDataDirectory();
        using var first = CreateServer(dir.Paths);
        await first.StartAsync(TestContext.Current.CancellationToken);
        await first.StopAsync(TestContext.Current.CancellationToken);
        using var second = CreateServer(dir.Paths);

        await second.StartAsync(TestContext.Current.CancellationToken);

        Assert.True((await StatusAsync(dir.Paths)).Ok);
        await second.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Malformed_request_gets_an_error_response()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);

        using var socket = await ConnectRawAsync(dir.Paths);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync("not json\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":false,\"error\":\"Malformed request.\"}", line);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Oversized_request_without_a_newline_is_rejected()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);

        using var socket = await ConnectRawAsync(dir.Paths);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Enumerable.Repeat((byte)'a', IpcServer.MaxRequestBytes).ToArray(), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":false,\"error\":\"Malformed request.\"}", line);
        Assert.True((await StatusAsync(dir.Paths)).Ok); // And the server carries on.
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_silent_client_does_not_block_other_clients_and_is_dropped_after_the_timeout()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths, clientTimeout: TimeSpan.FromMilliseconds(600));
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var silent = await ConnectRawAsync(dir.Paths); // Connects and says nothing.

        var started = DateTime.UtcNow;
        var response = await StatusAsync(dir.Paths);

        Assert.True(response.Ok);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(500), "A silent client delayed another client.");

        await using var stream = new NetworkStream(silent);
        var read = await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.Equal(0, read); // The server closed the silent connection after its timeout.
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Many_clients_are_served_concurrently()
    {
        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => StatusAsync(dir.Paths)));

        Assert.All(responses, r => Assert.True(r.Ok));
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Connections_beyond_the_limit_are_dropped_and_service_resumes_when_slots_free_up()
    {
        using var dir = new TempDataDirectory();
        using var allHeld = new CountdownEvent(IpcServer.MaxConnections);
        using var release = new ManualResetEventSlim(false);
        var handler = new EchoHandler
        {
            // Each held request signals that the server has accepted it and occupies a slot, then waits.
            Override = _ =>
            {
                if (!allHeld.IsSet)
                {
                    allHeld.Signal();
                }

                release.Wait(TestContext.Current.CancellationToken);
                return new IpcResponse(true);
            },
        };
        using var server = CreateServer(dir.Paths, handler, clientTimeout: TimeSpan.FromSeconds(30));
        await server.StartAsync(TestContext.Current.CancellationToken);

        var hogs = new List<Task>();
        for (var i = 0; i < IpcServer.MaxConnections; i++)
        {
            hogs.Add(HoldConnectionUntilClosedAsync(dir.Paths));
        }

        await Task.Run(() => allHeld.Wait(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => StatusAsync(dir.Paths));

        release.Set();
        await Task.WhenAll(hogs); // Each hog returns when the server closed its connection, after freeing the slot.

        Assert.True((await StatusAsync(dir.Paths)).Ok);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Sends a status request and reads until the server closes the connection.</summary>
    private static async Task HoldConnectionUntilClosedAsync(VigilPaths paths)
    {
        using var socket = await ConnectRawAsync(paths);
        var request = JsonSerializer.SerializeToUtf8Bytes(new IpcRequest(IpcCommand.Status), VigilJsonContext.Default.IpcRequest);
        await socket.SendAsync(request, TestContext.Current.CancellationToken);
        await socket.SendAsync("\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var buffer = new byte[1024];
        while (await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken) > 0)
        {
        }
    }

    [Fact]
    public async Task A_handler_exception_costs_one_request_not_the_ipc_server()
    {
        using var dir = new TempDataDirectory();
        var handler = new EchoHandler { Override = _ => throw new InvalidOperationException("boom") };
        using var server = CreateServer(dir.Paths, handler);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var failed = await StatusAsync(dir.Paths);
        handler.Override = null;
        var next = await StatusAsync(dir.Paths);

        Assert.False(failed.Ok);
        Assert.Contains("Internal error", failed.Error, StringComparison.Ordinal);
        Assert.True(next.Ok);
        Assert.True(File.Exists(dir.Paths.SocketPath));
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Client_reports_a_missing_daemon()
    {
        using var dir = new TempDataDirectory();

        await Assert.ThrowsAsync<DaemonNotRunningException>(() => StatusAsync(dir.Paths));
    }

    [Fact]
    public async Task Client_reports_a_stale_socket_as_not_running()
    {
        using var dir = new TempDataDirectory();
        using var crashedDaemon = LeaveStaleSocket(dir.Paths.SocketPath);

        await Assert.ThrowsAsync<DaemonNotRunningException>(() => StatusAsync(dir.Paths));
    }

    [Fact]
    public async Task Client_reports_the_real_error_when_access_is_denied()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // Root ignores file modes.
        }

        using var dir = new TempDataDirectory();
        using var server = CreateServer(dir.Paths);
        await server.StartAsync(TestContext.Current.CancellationToken);
        File.SetUnixFileMode(dir.Paths.SocketPath, UnixFileMode.None);

        var error = await Assert.ThrowsAsync<IOException>(() => StatusAsync(dir.Paths));

        Assert.Contains("AccessDenied", error.Message, StringComparison.Ordinal);
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A bound socket that never listens: the file exists and connecting is refused, like after a crash.</summary>
    private static Socket LeaveStaleSocket(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        return socket;
    }
}
