namespace Vigil.Core.Presence;

public enum PresenceState
{
    /// <summary>The user is active (or has been since the last injection); nothing to do.</summary>
    Watching,

    /// <summary>Threshold reached and an injection is due. Lasts until the injection result is recorded.</summary>
    Idle,

    /// <summary>The last injection was verified and the user has not been active since.</summary>
    Injected,

    /// <summary>Vigil is deliberately not acting; see <see cref="PauseReason"/>.</summary>
    Paused,

    /// <summary>Vigil could not reset the idle counter; see <see cref="DegradedReason"/>.</summary>
    Degraded,
}

public enum PauseReason
{
    Manual,
    OutsideSchedule,
    ScreenLocked,
    OnBattery,
}

public enum DegradedReason
{
    PermissionMissing,
    InjectionFailed,
    VerificationFailed,
}
