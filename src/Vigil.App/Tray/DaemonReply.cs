using Vigil.Core.Presence;

namespace Vigil.Tray;

/// <summary>What the daemon answered to the tray, or why it did not.</summary>
/// <param name="Reachable">False when there is no daemon to talk to (not running, or the socket does not answer).</param>
/// <param name="Error">Why the exchange did not work: the daemon's refusal, or the reason it could not be reached.</param>
/// <param name="RestartRequired">After a reload: the keys that apply only after the daemon is restarted.</param>
internal sealed record DaemonReply(
    bool Reachable,
    bool Ok,
    string? Error = null,
    PresenceStatus? Status = null,
    string? Platform = null,
    IReadOnlyList<string>? RestartRequired = null)
{
    public static DaemonReply Unreachable(string reason) => new(false, false, reason);
}
