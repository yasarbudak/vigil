using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Vigil.Ipc;

internal sealed class DaemonNotRunningException(string socketPath)
    : Exception($"The Vigil daemon is not running (no socket at {socketPath}). Start it with 'vigil run'.");

internal static class IpcClient
{
    /// <param name="timeout">How long to wait for the whole exchange; by default <see cref="IpcTimeouts.For"/> the command (5 seconds, except for the permission request).</param>
    public static async Task<IpcResponse> SendAsync(string socketPath, IpcRequest request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (Encoding.UTF8.GetByteCount(socketPath) > IpcServer.MaxSocketPathBytes)
        {
            // The same limit the daemon enforces: no daemon can listen here. Without this the socket class throws an
            // argument exception, which the commands and the tray would not expect.
            throw new IOException($"The socket path is too long for a Unix domain socket: {socketPath}");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? IpcTimeouts.For(request.Command));
        var timeoutToken = timeoutSource.Token;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutToken).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            // ECONNREFUSED (stale socket file) or ENOENT (no socket file): nobody is serving.
            throw new DaemonNotRunningException(socketPath);
        }
        catch (SocketException ex)
        {
            throw new IOException($"Cannot connect to the Vigil daemon at {socketPath}: {ex.Message} ({ex.SocketErrorCode}).", ex);
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, VigilJsonContext.Default.IpcRequest);
        await stream.WriteAsync(payload, timeoutToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), timeoutToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(timeoutToken).ConfigureAwait(false)
            ?? throw new IOException("The daemon closed the connection without a response.");

        return JsonSerializer.Deserialize(line, VigilJsonContext.Default.IpcResponse)
            ?? throw new IOException("The daemon sent an empty response.");
    }
}
