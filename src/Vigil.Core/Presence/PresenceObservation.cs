namespace Vigil.Core.Presence;

/// <summary>Everything the engine needs from the outside world for one decision.</summary>
public readonly record struct PresenceObservation(
    TimeSpan IdleTime,
    bool? IsScreenLocked,
    bool? IsOnBattery,
    bool CanPostEvents);

public readonly record struct PresenceDecision(PresenceState State, bool ShouldInject);
