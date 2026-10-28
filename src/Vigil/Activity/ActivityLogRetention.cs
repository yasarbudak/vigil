using Vigil.Core.Abstractions;

namespace Vigil.Activity;

/// <summary>
/// Applies the activity log's retention policy once per local calendar day: on the first call (daemon start)
/// and again on the first call after midnight. Cheap to call on every poll.
/// </summary>
internal sealed class ActivityLogRetention(IActivityLog log, TimeProvider time)
{
    private DateOnly? _lastRun;

    public void RunIfDue()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (_lastRun == today)
        {
            return;
        }

        _lastRun = today;
        log.Prune(today);
    }
}
