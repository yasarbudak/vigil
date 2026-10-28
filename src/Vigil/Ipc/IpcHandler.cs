using Vigil.Core.Presence;

namespace Vigil.Ipc;

/// <summary>Maps IPC commands onto the presence engine.</summary>
internal sealed class IpcHandler(PresenceEngine engine) : IIpcHandler
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

    private static IpcResponse Fail(string error) => new(false, error);
}
