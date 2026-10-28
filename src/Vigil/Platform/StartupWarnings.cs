using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Platform;

/// <summary>Conditions where the configuration promises something the platform cannot deliver. Shown at startup and by diag.</summary>
internal static class StartupWarnings
{
    public const string LockDetectionLost =
        "PauseOnLock is enabled but Vigil could not determine whether the screen is locked, so it will NOT pause while the screen is locked.";

    public const string LockDetectionRestored =
        "Vigil can determine whether the screen is locked again; PauseOnLock is in effect.";

    public static string? LockDetection(VigilOptions options, ISessionState session) =>
        LockDetection(options, session.IsScreenLocked());

    public static string? LockDetection(VigilOptions options, bool? isScreenLocked) =>
        options.PauseOnLock && isScreenLocked is null ? LockDetectionLost : null;
}
