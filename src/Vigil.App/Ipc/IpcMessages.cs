using Vigil.Core.Presence;

namespace Vigil.Ipc;

internal enum IpcCommand
{
    Status,
    Pause,
    PauseUntilTomorrow,
    Resume,

    /// <summary>Asks the daemon to stop. Sent by <c>vigil uninstall</c> on Windows, where there is no service manager to do it.</summary>
    Shutdown,
}

/// <summary>One request per connection, sent as a single JSON line.</summary>
internal sealed record IpcRequest(IpcCommand Command, TimeSpan? Duration = null);

/// <summary>One response per connection, sent as a single JSON line.</summary>
internal sealed record IpcResponse(bool Ok, string? Error = null, PresenceStatus? Status = null);

internal interface IIpcHandler
{
    IpcResponse Handle(IpcRequest request);

    /// <summary>
    /// Called by the server after the response to <paramref name="request"/> has been written, for effects that must
    /// not happen before the client has its answer (stopping the daemon would cut the connection). Does nothing by default.
    /// </summary>
    void ResponseSent(IpcRequest request)
    {
    }
}
