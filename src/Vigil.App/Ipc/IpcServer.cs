using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vigil.Ipc;

/// <summary>
/// Serves the CLI over a Unix domain socket: one JSON line in, one JSON line out, then the connection closes.
/// The socket is the only listening endpoint Vigil has.
/// </summary>
/// <remarks>
/// Ownership: the server holds an exclusive lock on <c>&lt;socket&gt;.lock</c> for its whole lifetime. That lock is
/// the single-instance mechanism, so only the lock holder ever deletes or recreates the socket file.
/// Access: the server makes sure the socket's directory is 0700 before binding, so there is no window in which
/// the socket is reachable by other users; the socket file itself is then set to 0600. On Windows there are no mode
/// bits: the socket file and the lock file take the access rules of the data directory (see <see cref="PrivateDirectory"/>).
/// AF_UNIX sockets need Windows 10 version 1803 or later; the path limit is 108 bytes there too.
/// It implements <see cref="IHostedService"/> directly so that acquiring the lock happens inside
/// <see cref="StartAsync"/> and a second daemon fails the host start before any other service runs.
/// </remarks>
internal sealed class IpcServer : IHostedService, IDisposable
{
    internal const int MaxRequestBytes = 4096;
    internal const int MaxConnections = 8;

    // sun_path is 104 bytes on macOS and 108 on Linux and Windows (afunix.h: UNIX_PATH_MAX 108, quoted in
    // dotnet/runtime UnixDomainSocketEndPoint.Windows.cs), so the same limit is safe on all three.
    internal const int MaxSocketPathBytes = 100;

    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly string _socketPath;
    private readonly IIpcHandler _handler;
    private readonly ILogger<IpcServer> _logger;
    private readonly TimeSpan _clientTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<Task, byte> _clients = new();

    private FileStream? _instanceLock;
    private Task _acceptLoop = Task.CompletedTask;

    public IpcServer(string socketPath, IIpcHandler handler, ILogger<IpcServer> logger, TimeSpan? clientTimeout = null)
    {
        _socketPath = socketPath;
        _handler = handler;
        _logger = logger;
        _clientTimeout = clientTimeout ?? IpcTimeouts.Default;
    }

    private string LockPath => _socketPath + ".lock";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(_socketPath) > MaxSocketPathBytes)
        {
            throw new InvalidOperationException($"The socket path is too long for a Unix domain socket: {_socketPath}");
        }

        PrivateDirectory.Ensure(Path.GetDirectoryName(_socketPath)
            ?? throw new InvalidOperationException($"The socket path has no directory: {_socketPath}"));

        _instanceLock = AcquireInstanceLock();
        try
        {
            var listener = Bind();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token), CancellationToken.None);
        }
        catch
        {
            _instanceLock.Dispose();
            _instanceLock = null;
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_clients.Keys).WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
        _instanceLock?.Dispose();
        _instanceLock = null;
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _connectionSlots.Dispose();
        _instanceLock?.Dispose();
    }

    private FileStream AcquireInstanceLock()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None, // Exclusive: on Unix an advisory flock, on Windows a share mode no other open can get past, held until the stream is closed.
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        // Windows: no mode bits; the lock file takes the data directory's access rules (see PrivateDirectory).

        try
        {
            return new FileStream(LockPath, options);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another Vigil daemon is already running (lock {LockPath}).", ex);
        }
    }

    private Socket Bind()
    {
        // Holding the lock means no other daemon owns the socket, so a file left here is stale.
        if (File.Exists(_socketPath))
        {
            _logger.LogInformation("Removing stale socket {Path}.", _socketPath);
            File.Delete(_socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Windows: no mode bits; the socket file takes the data directory's access rules (see PrivateDirectory).

            listener.Listen(backlog: MaxConnections);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        return listener;
    }

    /// <summary>Accepts until shutdown. An accept failure is logged and retried; it never ends the loop.</summary>
    private async Task AcceptLoopAsync(Socket listener, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Listening on {Path}.", _socketPath);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Accepting an IPC connection failed; retrying.");
                    await DelayQuietlyAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                Dispatch(client, stoppingToken);
            }
        }
        finally
        {
            listener.Dispose();
            File.Delete(_socketPath); // We own it: the instance lock is still held here.
        }
    }

    private void Dispatch(Socket client, CancellationToken stoppingToken)
    {
        if (!_connectionSlots.Wait(0, stoppingToken))
        {
            _logger.LogWarning("Too many IPC connections; dropped one.");
            client.Dispose();
            return;
        }

        // Task.Run: the synchronous part of serving (reading data that already arrived, running the handler) must
        // never run on the accept loop, or one slow client or handler would stall accepting and the limit.
        var task = Task.Run(() => ServeAndReleaseAsync(client, stoppingToken), CancellationToken.None);
        _clients[task] = 0;
        _ = task.ContinueWith(
            finished => _clients.TryRemove(finished, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DelayQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AcceptRetryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Frees the connection slot before the socket is closed, so a client that sees its connection end can rely on
    /// the slot being available again.
    /// </summary>
    private async Task ServeAndReleaseAsync(Socket client, CancellationToken stoppingToken)
    {
        try
        {
            await ServeAsync(client, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionSlots.Release();
            client.Dispose();
        }
    }

    /// <summary>Serves one client. Never throws: whatever goes wrong affects only this connection.</summary>
    private async Task ServeAsync(Socket client, CancellationToken stoppingToken)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);

            // The time limit guards the two things a slow or hostile client controls: sending the request and reading the
            // response. It does not run while the handler works, because a command that waits for a person (the permission
            // request) legitimately takes minutes and its handler bounds the wait itself; a handler that blocks was never
            // interrupted by this limit anyway.
            IpcRequest? request;
            using (var reading = CreateTimeLimit(stoppingToken))
            {
                request = await ReadRequestAsync(stream, reading.Token).ConfigureAwait(false);
            }

            IpcResponse response;
            if (request is null)
            {
                response = new IpcResponse(false, "Malformed request.");
            }
            else if (IpcTimeouts.IsLongRunning(request.Command))
            {
                var answered = await HandleWatchingClientAsync(request, stream, stoppingToken).ConfigureAwait(false);
                if (answered is null)
                {
                    return; // The client went away; the handler was cancelled and nobody is left to answer.
                }

                response = answered;
            }
            else
            {
                response = await HandleAsync(request, stoppingToken).ConfigureAwait(false);
            }

            using var writing = CreateTimeLimit(stoppingToken);
            var payload = JsonSerializer.SerializeToUtf8Bytes(response, VigilJsonContext.Default.IpcResponse);
            await stream.WriteAsync(payload, writing.Token).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), writing.Token).ConfigureAwait(false);

            if (request is not null && response.Ok)
            {
                NotifyResponseSent(request);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Dropped an IPC client: {Reason}", ex.Message);
        }
        catch (Exception)
        {
            // Shutting down; the client is simply closed.
        }
    }

    private void NotifyResponseSent(IpcRequest request)
    {
        try
        {
            _handler.ResponseSent(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finishing IPC command {Command} failed.", request.Command);
        }
    }

    /// <summary>
    /// Runs a handler that waits for a person while watching the connection. The protocol is one request line then one response,
    /// so the client sends nothing more; a read on the connection completes with zero bytes when the peer closed it
    /// (<see cref="Socket.Receive(byte[])"/> documents this for a remote host that shut the connection down: the call "will complete
    /// immediately and return zero bytes"), or fails when the connection broke. Either means the client is gone (Ctrl+C), and the
    /// handler's token is cancelled, which makes the portal request close and frees the slot. A client that is still there never
    /// completes the read, so nothing is read or lost; unexpected extra bytes are discarded.
    /// </summary>
    /// <returns>The response, or null when the client went away first.</returns>
    private async Task<IpcResponse?> HandleWatchingClientAsync(IpcRequest request, NetworkStream stream, CancellationToken stoppingToken)
    {
        using var handling = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watching = WatchForDisconnectAsync(stream, handling);
        try
        {
            return await HandleAsync(request, handling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && handling.IsCancellationRequested)
        {
            _logger.LogInformation("The client of IPC command {Command} went away; the request was cancelled.", request.Command);
            return null;
        }
        finally
        {
            await handling.CancelAsync().ConfigureAwait(false); // ends the watch if the handler finished first
            await watching.ConfigureAwait(false);
        }
    }

    private static async Task WatchForDisconnectAsync(NetworkStream stream, CancellationTokenSource handling)
    {
        var buffer = new byte[64];
        try
        {
            while (!handling.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, handling.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The connection broke: the client is gone as well.
        }
        catch (OperationCanceledException)
        {
            return; // The handler finished or the daemon is stopping: nothing to report.
        }

        await handling.CancelAsync().ConfigureAwait(false);
    }

    private CancellationTokenSource CreateTimeLimit(CancellationToken stoppingToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        limit.CancelAfter(_clientTimeout);
        return limit;
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // The daemon is stopping or the client went away; the caller decides, nothing to answer.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling IPC command {Command} failed.", request.Command);
            return new IpcResponse(false, "Internal error; see the daemon log.");
        }
    }

    private static async Task<IpcRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                try
                {
                    return JsonSerializer.Deserialize(buffer.AsSpan(0, newline), VigilJsonContext.Default.IpcRequest);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null; // No newline within the size limit.
    }
}
