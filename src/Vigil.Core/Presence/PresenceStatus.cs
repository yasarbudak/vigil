namespace Vigil.Core.Presence;

/// <summary>Point-in-time view of the engine, safe to hand to the CLI or a tray UI.</summary>
public sealed record PresenceStatus(
    PresenceState State,
    PauseReason? PauseReason,
    DegradedReason? DegradedReason,
    DateTimeOffset? PausedUntil,
    DateTimeOffset? LastRealActivity,
    DateTimeOffset? LastInjection,
    int InjectionsToday,
    TimeSpan CoveredToday,
    DateTimeOffset At);
