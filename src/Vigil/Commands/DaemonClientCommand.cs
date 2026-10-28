using Vigil.Ipc;

namespace Vigil.Commands;

/// <summary>Shared flow for commands that send one request to the daemon and print the resulting status.</summary>
internal static class DaemonClientCommand
{
    public static async Task<int> ExecuteAsync(VigilPaths paths, IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await IpcClient.SendAsync(paths.SocketPath, request, cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.Status is null)
            {
                await Console.Error.WriteLineAsync($"vigil: {response.Error ?? "The daemon reported an error."}").ConfigureAwait(false);
                return 1;
            }

            StatusFormatter.Write(Console.Out, response.Status);
            return 0;
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"vigil: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
