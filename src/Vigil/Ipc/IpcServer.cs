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
/// the socket is reachable by other users; the socket file itself is then set to 0600.
/// It implements <see cref="IHostedService"/> directly so that acquiring the lock happens inside
/// <see cref="StartAsync"/> and a second daemon fails the host start before any other service runs.
/// </remarks>
internal sealed class IpcServer : IHostedService, IDisposable
{
    internal const int MaxRequestBytes = 4096;
    internal const int MaxConnections = 8;

    private const int MaxSocketPathBytes = 100; // sun_path is 104 bytes on macOS, 108 on Linux.
    private static readonly TimeSpan DefaultClientTimeout = TimeSpan.FromSeconds(5);
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
        _clientTimeout = clientTimeout ?? DefaultClientTimeout;
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
            Share = FileShare.None, // Exclusive: on Unix this is an advisory flock held until the stream is closed.
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

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

        var task = ServeAsync(client, stoppingToken);
        _clients[task] = 0;
        _ = task.ContinueWith(
            finished =>
            {
                _clients.TryRemove(finished, out _);
                _connectionSlots.Release();
            },
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

    /// <summary>Serves one client. Never throws: whatever goes wrong affects only this connection.</summary>
    private async Task ServeAsync(Socket client, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_clientTimeout);

        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            var response = request is null ? new IpcResponse(false, "Malformed request.") : Handle(request);

            var payload = JsonSerializer.SerializeToUtf8Bytes(response, VigilJsonContext.Default.IpcResponse);
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
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

    private IpcResponse Handle(IpcRequest request)
    {
        try
        {
            return _handler.Handle(request);
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
