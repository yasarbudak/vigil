using Microsoft.Extensions.Hosting;
using Vigil.Core.Presence;

namespace Vigil.Ipc;

/// <summary>Maps IPC commands onto the presence engine and the daemon's lifetime.</summary>
internal sealed class IpcHandler(PresenceEngine engine, IHostApplicationLifetime lifetime) : IIpcHandler
{
    public IpcResponse Handle(IpcRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommand.Status:
                    break;
                case IpcCommand.Pause when request.Duration is { } duration:
                    engine.Pause(duration);
                    break;
                case IpcCommand.Pause:
                    return Fail("Pause needs a duration.");
                case IpcCommand.PauseUntilTomorrow:
                    engine.PauseUntilTomorrow();
                    break;
                case IpcCommand.Resume:
                    engine.Resume();
                    break;
                case IpcCommand.Shutdown:
                    break; // Acknowledged here; the stop itself happens in ResponseSent, after the client has the answer.
                default:
                    return Fail($"Unknown command '{request.Command}'.");
            }
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        return new IpcResponse(true, Status: engine.GetStatus());
    }

    public void ResponseSent(IpcRequest request)
    {
        if (request.Command == IpcCommand.Shutdown)
        {
            lifetime.StopApplication();
        }
    }

    private static IpcResponse Fail(string error) => new(false, error);
}
