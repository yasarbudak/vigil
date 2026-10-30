namespace Vigil.Platform;

internal enum LockDetectionChange
{
    None,

    /// <summary>The lock state could be read before and cannot be now.</summary>
    Lost,

    /// <summary>The lock state could not be read before and can be now.</summary>
    Restored,
}

/// <summary>
/// Turns the per-poll "can the lock state be read" fact into transitions, so a change is reported once
/// and not on every poll.
/// </summary>
internal sealed class LockDetectionMonitor(bool initiallyKnown)
{
    private bool _known = initiallyKnown;

    public LockDetectionChange Observe(bool known)
    {
        if (known == _known)
        {
            return LockDetectionChange.None;
        }

        _known = known;
        return known ? LockDetectionChange.Restored : LockDetectionChange.Lost;
    }
}
