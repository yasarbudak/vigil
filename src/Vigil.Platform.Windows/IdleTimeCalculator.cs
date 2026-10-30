namespace Vigil.Platform.Windows;

/// <summary>
/// Turns the two 32-bit tick counts of GetTickCount and GetLastInputInfo into an idle time. Pure: no Win32 calls.
/// </summary>
internal static class IdleTimeCalculator
{
    /// <summary>
    /// Time between the tick count of the last input and the current tick count, both in milliseconds since system
    /// start as 32-bit values. They wrap to zero every 49.7 days (GetTickCount remarks), so the difference is taken
    /// modulo 2^32: it stays right across a wrap, and it is the idiom of the documentation's own example
    /// (<c>currentTime - m_startTime</c> on DWORDs).
    /// </summary>
    /// <remarks>
    /// The last-input tick count is "not guaranteed to be incremental": it can be a few milliseconds ahead of the tick
    /// count read a moment later, because an event raised by SendInput supplies its own tick count
    /// (GetLastInputInfo remarks). Modulo 2^32 that looks like an idle time of almost 49.7 days, so a difference of
    /// 2^31 ms (24.9 days) or more is read as "the last input is slightly in the future" and gives zero. The price: a
    /// real idle time of more than 24.9 days is reported as zero, which only means Vigil does not inject. It would
    /// have injected long before that.
    /// https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount
    /// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo
    /// </remarks>
    public static TimeSpan Elapsed(uint currentTick, uint lastInputTick)
    {
        var milliseconds = unchecked(currentTick - lastInputTick);
        return milliseconds <= int.MaxValue ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;
    }
}
