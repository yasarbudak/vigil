using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Platform;

/// <summary>Conditions where the configuration promises something the platform cannot deliver. Shown at startup and by diag.</summary>
internal static class StartupWarnings
{
    public static string? LockDetection(VigilOptions options, ISessionState session) =>
        options.PauseOnLock && session.IsScreenLocked() is null
            ? "PauseOnLock is enabled but this platform cannot detect a locked screen, so Vigil will NOT pause while the screen is locked."
            : null;
}
