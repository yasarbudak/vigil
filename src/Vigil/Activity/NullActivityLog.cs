using Vigil.Core.Abstractions;

namespace Vigil.Activity;

/// <summary>Used when <c>ActivityLog.Enabled</c> is false: nothing is stored or read.</summary>
internal sealed class NullActivityLog : IActivityLog
{
    public void Record(ActivityEntry entry)
    {
    }

    public DailyTotals ReadTotals(DateOnly day) => default;
}
