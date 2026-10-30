using Vigil.Ipc;

namespace Vigil.Tray;

internal enum SaveOutcomeKind
{
    /// <summary>The settings are not valid; nothing was written.</summary>
    Invalid,

    /// <summary>Saved, and the running daemon applied them.</summary>
    Applied,

    /// <summary>Saved and applied, except for the keys in <see cref="SaveOutcome.RestartRequired"/>.</summary>
    AppliedRestartNeeded,

    /// <summary>Saved. No daemon is running, so the settings apply when it starts.</summary>
    SavedDaemonNotRunning,

    /// <summary>Saved, but the daemon refused the file when it was asked to reload it, and kept running with the old settings.</summary>
    SavedDaemonRefused,
}

/// <param name="Messages">What the window shows: the reasons for <see cref="SaveOutcomeKind.Invalid"/> and <see cref="SaveOutcomeKind.SavedDaemonRefused"/>, a sentence otherwise.</param>
internal sealed record SaveOutcome(SaveOutcomeKind Kind, IReadOnlyList<string> Messages, IReadOnlyList<string> RestartRequired)
{
    public bool Saved => Kind != SaveOutcomeKind.Invalid;
}

/// <summary>
/// Save, then tell the daemon: the settings window's Save button. The file is written first and only then is the daemon asked
/// to reload it, so config.json is always what the daemon is told to read, and a daemon that is not running simply reads it
/// when it starts.
/// </summary>
internal sealed class SettingsController(ISettingsStore store, IDaemonClient daemon)
{
    public async Task<SaveOutcome> SaveAsync(SettingsForm form, CancellationToken cancellationToken)
    {
        var saved = store.Save(form);
        if (!saved.Saved)
        {
            return new SaveOutcome(SaveOutcomeKind.Invalid, saved.Errors, []);
        }

        var reply = await daemon.SendAsync(TrayCommands.Reload(), cancellationToken).ConfigureAwait(false);
        if (!reply.Reachable)
        {
            return new SaveOutcome(SaveOutcomeKind.SavedDaemonNotRunning, ["Saved. The Vigil daemon is not running; the settings apply when it starts."], []);
        }

        if (!reply.Ok)
        {
            return new SaveOutcome(SaveOutcomeKind.SavedDaemonRefused, [reply.Error ?? "The daemon refused the new settings."], []);
        }

        return reply.RestartRequired is { Count: > 0 } restart
            ? new SaveOutcome(SaveOutcomeKind.AppliedRestartNeeded, [$"Saved and applied. Restart the Vigil daemon to apply: {string.Join(", ", restart)}."], restart)
            : new SaveOutcome(SaveOutcomeKind.Applied, ["Saved and applied."], []);
    }
}
