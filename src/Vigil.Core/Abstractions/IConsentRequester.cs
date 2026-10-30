namespace Vigil.Core.Abstractions;

/// <summary>How a consent request ended.</summary>
public enum ConsentOutcome
{
    /// <summary>The consent covers what Vigil needs and was saved.</summary>
    Granted,

    /// <summary>The consent was given but not for what Vigil needs, or it could not be used (no device, no restore token).</summary>
    Denied,

    /// <summary>The dialog was cancelled. The portal reports a refusal and a cancelled dialog alike (Request.Response 1).</summary>
    Cancelled,

    /// <summary>Nobody answered in time; the request was closed.</summary>
    TimedOut,

    /// <summary>Another consent request is still waiting for its answer.</summary>
    InProgress,

    /// <summary>The request could not be run (no portal, the portal ended the request, the token could not be saved, the daemon is stopping).</summary>
    Failed,
}

/// <param name="Message">What to tell the user; says what to do next when the outcome is not <see cref="ConsentOutcome.Granted"/>.</param>
public sealed record ConsentResult(ConsentOutcome Outcome, string Message)
{
    public bool Granted => Outcome == ConsentOutcome.Granted;
}

/// <summary>
/// Asks the OS for a consent in the process that has to use it (the daemon), because the OS ties the consent to the caller's
/// identity. May show a dialog, so it is only run on an explicit request (<c>vigil permissions request</c> over IPC).
/// </summary>
public interface IConsentRequester
{
    /// <summary>Runs the consent flow and waits for the user. Every outcome is a result; only a cancelled token ends it early, with <see cref="OperationCanceledException"/>.</summary>
    Task<ConsentResult> RequestConsentAsync(CancellationToken cancellationToken);
}
