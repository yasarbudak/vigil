namespace Vigil.Daemon;

/// <summary>
/// Keeps a failure that repeats at every poll from filling the log. A session that is not there yet (no display, a compositor
/// that is still starting) fails every <c>PollInterval</c> with the same message, which at 5 s would be 17,000 lines a day.
/// The first occurrence is logged; the same failure again is not; a different failure is, and so is the first poll that
/// works after any failure (<see cref="Recovered"/>). Two failures are "the same" when their type and message are equal.
/// </summary>
internal sealed class RepeatedFailureTracker
{
    private (Type Type, string Message)? _last;

    /// <returns>True when this failure is new or differs from the previous one and should be logged.</returns>
    public bool ShouldLog(Exception failure)
    {
        var current = (failure.GetType(), failure.Message);
        if (_last == current)
        {
            return false;
        }

        _last = current;
        return true;
    }

    /// <summary>Call after a poll that worked; true exactly once after a run of failures.</summary>
    public bool Recovered()
    {
        if (_last is null)
        {
            return false;
        }

        _last = null;
        return true;
    }
}
