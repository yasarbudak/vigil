using Vigil.Core.Abstractions;

namespace Vigil.Activity;

/// <summary>
/// The activity log the daemon runs with: forwards to the current implementation, which <see cref="ConfigReloader"/> swaps when
/// <c>ActivityLog.Enabled</c> or <c>ActivityLog.RetentionDays</c> change, so the engine and the retention job never hold a stale one.
/// </summary>
internal sealed class ReloadableActivityLog(IActivityLog initial) : IActivityLog
{
    private volatile IActivityLog _current = initial;

    /// <summary>The implementation in use now (tests check which one the configuration selected).</summary>
    public IActivityLog Current => _current;

    /// <summary>Takes effect for the next call; a call already running finishes on the previous implementation.</summary>
    public void Replace(IActivityLog next) => _current = next;

    public void Record(ActivityEntry entry) => _current.Record(entry);

    public DailyTotals ReadTotals(DateOnly day) => _current.ReadTotals(day);

    public void Prune(DateOnly today) => _current.Prune(today);
}
