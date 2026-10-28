using Vigil.Core.Presence;

namespace Vigil.Ipc;

internal enum IpcCommand
{
    Status,
    Pause,
    PauseUntilTomorrow,
    Resume,
}

/// <summary>One request per connection, sent as a single JSON line.</summary>
internal sealed record IpcRequest(IpcCommand Command, TimeSpan? Duration = null);

/// <summary>One response per connection, sent as a single JSON line.</summary>
internal sealed record IpcResponse(bool Ok, string? Error = null, PresenceStatus? Status = null);

internal interface IIpcHandler
{
    IpcResponse Handle(IpcRequest request);
}
