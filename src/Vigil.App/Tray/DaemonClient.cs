using System.Text.Json;
using Vigil.Ipc;

namespace Vigil.Tray;

/// <summary>The tray's only way to the daemon: one request, one answer, over the local IPC socket.</summary>
internal interface IDaemonClient
{
    Task<DaemonReply> SendAsync(IpcRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a request through <paramref name="transport"/> (<see cref="IpcClient"/> in the tray, a fake in tests) and turns every
/// way it can end into a <see cref="DaemonReply"/>, so the tray never has to handle an exception: no daemon, a socket that does
/// not answer in time, a refusal.
/// </summary>
internal sealed class DaemonClient(Func<IpcRequest, CancellationToken, Task<IpcResponse>> transport) : IDaemonClient
{
    public static DaemonClient ForSocket(string socketPath) =>
        new((request, cancellationToken) => IpcClient.SendAsync(socketPath, request, cancellationToken));

    public async Task<DaemonReply> SendAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await transport(request, cancellationToken).ConfigureAwait(false);
            return response.Ok
                ? new DaemonReply(true, true, Status: response.Status, Platform: response.Platform, RestartRequired: response.RestartRequired)
                : new DaemonReply(true, false, response.Error ?? "The daemon reported an error.");
        }
        catch (DaemonNotRunningException ex)
        {
            return DaemonReply.Unreachable(ex.Message);
        }
        catch (IOException ex)
        {
            return DaemonReply.Unreachable(ex.Message);
        }
        catch (JsonException ex)
        {
            // Something else is listening at the socket, or a daemon of another version answers in a form this one cannot read.
            return DaemonReply.Unreachable($"The answer of the Vigil daemon cannot be read: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own time limit: the socket exists but nobody answered.
            return DaemonReply.Unreachable("The Vigil daemon did not answer in time.");
        }
    }
}
