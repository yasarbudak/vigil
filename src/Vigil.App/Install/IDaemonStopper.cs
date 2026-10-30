using Vigil.Ipc;

namespace Vigil.Install;

internal enum DaemonStopOutcome
{
    /// <summary>No daemon answered on the socket, so there was nothing to stop.</summary>
    NotRunning,

    /// <summary>A daemon was asked to stop and is gone.</summary>
    Stopped,

    /// <summary>A daemon was there and could not be stopped; <see cref="DaemonStopResult.Detail"/> says why.</summary>
    Failed,
}

internal readonly record struct DaemonStopResult(DaemonStopOutcome Outcome, string? Detail = null);

/// <summary>Stops the running daemon. Behind an interface so tests never talk to a real daemon.</summary>
internal interface IDaemonStopper
{
    DaemonStopResult Stop();
}

/// <summary>
/// Stops the daemon that serves this data directory by asking it over its IPC socket (<see cref="IpcCommand.Shutdown"/>),
/// then waits until the socket stops answering. The daemon shuts down the way it does for Ctrl+C: stops accepting,
/// finishes the poll in progress, releases the instance lock and closes its log. It is whichever daemon owns the
/// socket, whether autostart started it or someone ran <c>vigil run</c>.
/// </summary>
internal sealed class IpcDaemonStopper(string socketPath, TimeSpan? timeout = null) : IDaemonStopper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public DaemonStopResult Stop()
    {
        var limit = timeout ?? DefaultTimeout;
        using var cancellation = new CancellationTokenSource(limit);
        try
        {
            var response = IpcClient.SendAsync(socketPath, new IpcRequest(IpcCommand.Shutdown), cancellation.Token).GetAwaiter().GetResult();
            if (!response.Ok)
            {
                return new DaemonStopResult(DaemonStopOutcome.Failed, response.Error ?? "The daemon refused to stop.");
            }

            while (!cancellation.IsCancellationRequested)
            {
                if (!IsAnswering(cancellation.Token))
                {
                    return new DaemonStopResult(DaemonStopOutcome.Stopped);
                }

                Task.Delay(PollInterval, cancellation.Token).GetAwaiter().GetResult();
            }
        }
        catch (DaemonNotRunningException)
        {
            return new DaemonStopResult(DaemonStopOutcome.NotRunning);
        }
        catch (IOException ex)
        {
            return new DaemonStopResult(DaemonStopOutcome.Failed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // The time limit ran out; reported below.
        }

        return new DaemonStopResult(DaemonStopOutcome.Failed, $"The daemon was asked to stop and still answered after {limit.TotalSeconds:0} seconds.");
    }

    private bool IsAnswering(CancellationToken cancellationToken)
    {
        try
        {
            IpcClient.SendAsync(socketPath, new IpcRequest(IpcCommand.Status), cancellationToken).GetAwaiter().GetResult();
            return true;
        }
        catch (DaemonNotRunningException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // Something is there but not well; treat it as still running and keep waiting for the limit.
        }
    }
}
