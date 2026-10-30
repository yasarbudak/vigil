namespace Vigil.Platform.Linux;

/// <summary>Turns the unsigned millisecond counts of the X server and of mutter into a <see cref="TimeSpan"/> without overflowing.</summary>
internal static class IdleMilliseconds
{
    /// <summary>Values above what a <see cref="TimeSpan"/> holds (about 292 million years) are capped; no real idle time comes near.</summary>
    public static TimeSpan ToTimeSpan(ulong milliseconds) =>
        milliseconds >= (ulong)TimeSpan.MaxValue.TotalMilliseconds ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);
}
