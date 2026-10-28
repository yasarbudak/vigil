namespace Vigil.Core.Presence;

/// <summary>Point-in-time view of the engine, safe to hand to the CLI or a tray UI.</summary>
/// <param name="PauseOnLock">Whether the configuration asks Vigil to pause while the screen is locked.</param>
/// <param name="LockStateKnown">
/// Whether the latest observation could tell if the screen is locked; null before the first observation.
/// False with <paramref name="PauseOnLock"/> means the lock pause cannot take effect and Vigil keeps injecting.
/// </param>
public sealed record PresenceStatus(
    PresenceState State,
    PauseReason? PauseReason,
    DegradedReason? DegradedReason,
    DateTimeOffset? PausedUntil,
    DateTimeOffset? LastRealActivity,
    DateTimeOffset? LastInjection,
    int InjectionsToday,
    TimeSpan CoveredToday,
    DateTimeOffset At,
    bool PauseOnLock = false,
    bool? LockStateKnown = null);
