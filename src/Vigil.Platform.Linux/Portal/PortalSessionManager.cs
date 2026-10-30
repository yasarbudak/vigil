using Vigil.Core.Abstractions;
using Vigil.Platform.Linux.DBus;

namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// Owns the daemon's portal session and the saved restore token, and enforces the rule that only
/// <c>vigil permissions request</c> may show a consent dialog. The request reaches the daemon over IPC and runs here
/// (<see cref="RequestConsentAsync"/>), in the daemon's process, because the portal ties a restore token to the identity of the
/// process that asked.
/// <list type="bullet">
/// <item>The daemon never asks for consent on its own. It starts a session only when a token has been saved, and it gives the portal a
/// short time (<see cref="PortalTimeouts.StartSilent"/>): with a valid token the answer is immediate, so no answer means a
/// dialog was about to be shown, and the request is closed (which removes the dialog) and the token dropped. The portal
/// itself decides whether a token is still valid ("when the stored permissions are withdrawn ... the user will be prompted
/// normally"), and Vigil cannot ask it beforehand: this closing is the nearest Vigil can come to "never prompts", and a
/// revoked consent can still make a dialog flash for up to that time. Documented in the README.</item>
/// <item>A saved token that the portal did not accept is deleted, because it is used up or invalid; no further attempt can show
/// a dialog until the user runs the request command again. Without a token the probe says "not granted", which the engine
/// shows as <c>Degraded (PermissionMissing)</c>.</item>
/// <item>Tokens are single use, so every started session writes the token the portal returned.</item>
/// </list>
/// </summary>
internal sealed class PortalSessionManager(PortalClient client, PortalTokenStore tokens, LinuxOptions options) : IDisposable
{
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private PortalSession? _session;
    private string? _warning;
    private bool _consentActive; // guarded by _gate; one consent request at a time

    /// <summary>Never prompts and never touches the bus: an open session, or a saved token the daemon can try.</summary>
    public bool CanPostEvents()
    {
        lock (_gate)
        {
            return _session is { IsClosed: false } || tokens.Exists();
        }
    }

    public InjectionResult Inject()
    {
        lock (_gate)
        {
            if (_session is { IsClosed: true })
            {
                _session.Dispose();
                _session = null;
            }

            if (_session is null && _consentActive)
            {
                // The saved token belongs to the request that is waiting for the user; using it here would use it up.
                return InjectionResult.Failure("A permission request is waiting for an answer.");
            }

            if (_session is null && OpenWithSavedToken() is { } failure)
            {
                return failure;
            }

            return Send(_session!);
        }
    }

    /// <summary>
    /// The one place a consent dialog may be shown. Waits for the user, up to <see cref="PortalTimeouts.StartInteractive"/> or until
    /// <paramref name="cancellationToken"/> ends it (then <see cref="OperationCanceledException"/>). Run in the daemon, only for <c>vigil permissions request</c> (IPC). One request runs
    /// at a time; another one gets <see cref="ConsentOutcome.InProgress"/>. On success the new session replaces the open one and is
    /// the one the daemon injects through.
    /// </summary>
    public async Task<ConsentResult> RequestConsentAsync(CancellationToken cancellationToken)
    {
        string? saved;
        lock (_gate) // waits for a start that is running; Inject refuses to start another while this flag is set
        {
            if (_consentActive)
            {
                return new ConsentResult(ConsentOutcome.InProgress, "A permission request is already in progress. Answer its dialog, or wait for it to end.");
            }

            _consentActive = true;
            saved = tokens.Read();
        }

        try
        {
            return await EstablishConsentAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _consentActive = false;
            }
        }
    }

    private async Task<ConsentResult> EstablishConsentAsync(string? saved, CancellationToken cancellationToken)
    {
        var result = await client.EstablishAsync(saved, PortalInteraction.Interactive, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != PortalOutcome.Established)
        {
            DropUsedUpToken(result);
            return new ConsentResult(
                result.Outcome switch
                {
                    PortalOutcome.Cancelled => ConsentOutcome.Cancelled,
                    PortalOutcome.TimedOut => ConsentOutcome.TimedOut,
                    _ => ConsentOutcome.Failed,
                },
                result.Message);
        }

        var session = result.Session!;
        var needed = options.Strategy == InjectionStrategy.F15KeyUp ? PortalProtocol.KeyboardDevice : PortalProtocol.PointerDevice;
        if ((session.Devices & needed) == 0)
        {
            session.Dispose();
            tokens.Delete();
            return new ConsentResult(
                ConsentOutcome.Denied,
                $"The consent does not include the {(needed == PortalProtocol.KeyboardDevice ? "keyboard" : "pointer")}, which Linux.Strategy={options.Strategy} needs. Run the command again and allow it.");
        }

        if (result.RestoreToken is null)
        {
            session.Dispose();
            tokens.Delete();
            return new ConsentResult(
                ConsentOutcome.Failed,
                "The consent was given, but the portal returned no restore token (its backend does not support persistent permissions), so the daemon could not start again without asking. Use Linux.Backend=Uinput instead.");
        }

        try
        {
            tokens.Write(result.RestoreToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Dispose();
            return new ConsentResult(ConsentOutcome.Failed, $"The consent was given, but the restore token could not be saved to {tokens.Path}: {ex.Message}");
        }

        PortalSession? replaced;
        lock (_gate)
        {
            replaced = _session;
            _session = session;
            _warning = null;
        }

        replaced?.Dispose();
        return new ConsentResult(ConsentOutcome.Granted, $"The consent is saved in {tokens.Path} and the daemon is using it now.");
    }

    public IReadOnlyList<LinuxDetail> Diagnose()
    {
        lock (_gate)
        {
            var details = new List<LinuxDetail>
            {
                new("Portal consent:", tokens.Exists() ? "saved" : "none saved (run 'vigil permissions request')"),
                new("Portal session:", _session is { IsClosed: false } s ? $"open ({DescribeDevices(s.Devices)})" : "not open"),
            };
            if (_warning is not null)
            {
                details.Add(new LinuxDetail("Portal warning:", _warning));
            }

            return details;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <returns>A failure to report, or null when <see cref="_session"/> is open.</returns>
    private InjectionResult? OpenWithSavedToken()
    {
        var token = tokens.Read();
        if (token is null)
        {
            return InjectionResult.Failure("No saved portal consent. Run 'vigil permissions request'.");
        }

        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var result = client.EstablishAsync(token, PortalInteraction.Silent, limit.Token).GetAwaiter().GetResult();
        if (result.Outcome != PortalOutcome.Established)
        {
            var dropped = DropUsedUpToken(result);
            return InjectionResult.Failure(dropped
                ? $"{result.Message} The saved consent was not accepted and was removed; run 'vigil permissions request' again."
                : result.Message);
        }

        _session = result.Session;
        try
        {
            if (result.RestoreToken is { } renewed)
            {
                if (!string.Equals(renewed, token, StringComparison.Ordinal))
                {
                    tokens.Write(renewed);
                }
            }
            else
            {
                tokens.Delete(); // The portal did not renew it; the old one is used up.
                _warning = "The portal returned no new restore token; after the next restart the consent has to be given again.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warning = $"The restore token could not be saved: {ex.Message}";
        }

        return null;
    }

    /// <summary>A token that was sent and not accepted is gone at the portal; keeping the file would only invite another dialog.</summary>
    private bool DropUsedUpToken(PortalEstablishResult result)
    {
        if (result.TokenSent && result.Outcome is PortalOutcome.Cancelled or PortalOutcome.Ended or PortalOutcome.TimedOut)
        {
            return tokens.Delete();
        }

        return false;
    }

    private InjectionResult Send(PortalSession session)
    {
        using var limit = new CancellationTokenSource(CallLimit);
        try
        {
            switch (options.Strategy)
            {
                case InjectionStrategy.MouseNudge:
                    if ((session.Devices & PortalProtocol.PointerDevice) == 0)
                    {
                        return InjectionResult.Failure("The portal consent does not include the pointer. Run 'vigil permissions request' and allow it, or set Linux.Strategy to F15KeyUp.");
                    }

                    // Two relative moves of one logical pixel, out and back (see InjectionStrategy.MouseNudge).
                    session.SendAsync(PortalProtocol.NotifyPointerMotion(session.Path, 1, 0), limit.Token).GetAwaiter().GetResult();
                    session.SendAsync(PortalProtocol.NotifyPointerMotion(session.Path, -1, 0), limit.Token).GetAwaiter().GetResult();
                    return InjectionResult.Success;

                case InjectionStrategy.F15KeyUp:
                    if ((session.Devices & PortalProtocol.KeyboardDevice) == 0)
                    {
                        return InjectionResult.Failure("The portal consent does not include the keyboard. Run 'vigil permissions request' and allow it, or set Linux.Strategy to MouseNudge.");
                    }

                    session.SendAsync(PortalProtocol.NotifyKeyboardKeysym(session.Path, Keysyms.F15, PortalProtocol.KeyReleased), limit.Token).GetAwaiter().GetResult();
                    return InjectionResult.Success;

                default:
                    return InjectionResult.Failure($"Unknown injection strategy '{options.Strategy}'.");
            }
        }
        catch (DBusFailureException ex)
        {
            return InjectionResult.Failure($"The portal call failed ({ex.Message}); the session will be opened again at the next attempt.");
        }
        catch (OperationCanceledException)
        {
            session.MarkClosed();
            return InjectionResult.Failure("The portal did not answer in time; the session will be opened again at the next attempt.");
        }
    }

    private static string DescribeDevices(uint devices) => devices switch
    {
        _ when (devices & (PortalProtocol.PointerDevice | PortalProtocol.KeyboardDevice)) == (PortalProtocol.PointerDevice | PortalProtocol.KeyboardDevice) => "pointer and keyboard",
        _ when (devices & PortalProtocol.PointerDevice) != 0 => "pointer only",
        _ when (devices & PortalProtocol.KeyboardDevice) != 0 => "keyboard only",
        _ => "no devices",
    };
}
