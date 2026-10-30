using System.Net.Sockets;
using System.Text.Json;

namespace Vigil.Ipc;

internal sealed class DaemonNotRunningException(string socketPath)
    : Exception($"The Vigil daemon is not running (no socket at {socketPath}). Start it with 'vigil run'.");

internal static class IpcClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<IpcResponse> SendAsync(string socketPath, IpcRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
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
        await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("The daemon closed the connection without a response.");

        return JsonSerializer.Deserialize(line, VigilJsonContext.Default.IpcResponse)
            ?? throw new IOException("The daemon sent an empty response.");
    }
}
