namespace Vigil.Core.Options;

public sealed class ActivityLogOptions
{
    /// <summary>
    /// Persist daily counters (number of injections, time covered) to the per-user data directory.
    /// When false nothing is written; counters then live in memory only and restart at zero.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
