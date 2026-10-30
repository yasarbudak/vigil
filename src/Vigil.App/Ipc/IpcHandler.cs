using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Abstractions;
using Vigil.Core.Presence;
using Vigil.Platform;

namespace Vigil.Ipc;

/// <summary>Maps IPC commands onto the presence engine and the daemon's lifetime.</summary>
/// <param name="consentLimit">How long a permission request may wait for the user before the daemon answers "timed out"; tests pass a short one.</param>
internal sealed class IpcHandler(PresenceEngine engine, IHostApplicationLifetime lifetime, PlatformServices? platform = null, TimeSpan? consentLimit = null, ILogger<IpcHandler>? logger = null) : IIpcHandler
{
    public IpcResponse Handle(IpcRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommand.Status:
                    break;
                case IpcCommand.Pause when request.Duration is { } duration:
                    engine.Pause(duration);
                    break;
                case IpcCommand.Pause:
                    return Fail("Pause needs a duration.");
                case IpcCommand.PauseUntilTomorrow:
                    engine.PauseUntilTomorrow();
                    break;
                case IpcCommand.Resume:
                    engine.Resume();
                    break;
                case IpcCommand.Shutdown:
                    break; // Acknowledged here; the stop itself happens in ResponseSent, after the client has the answer.
                case IpcCommand.RequestPermission:
                    return Fail("The permission request waits for a person and is served by HandleAsync.");
                default:
                    return Fail($"Unknown command '{request.Command}'.");
            }
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        return new IpcResponse(true, Status: engine.GetStatus(), Platform: platform?.Summary);
    }

    /// <summary>
    /// The only way a consent dialog can start in the daemon: <see cref="IpcCommand.RequestPermission"/>, sent by
    /// <c>vigil permissions request</c>. Every other command is the quick <see cref="Handle"/>.
    /// </summary>
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Command != IpcCommand.RequestPermission)
        {
            return Handle(request);
        }

        if (platform?.DaemonConsent is not { } consent)
        {
            // macOS (the Accessibility prompt is attributed to the bundle and is requested in the CLI process), Windows, and Linux
            // with X11 or uinput have nothing for the daemon to ask.
            return Fail("This daemon has no permission to request on this platform.");
        }

        var log = logger ?? NullLogger<IpcHandler>.Instance;
        log.LogInformation("Permission request received; asking for the consent now.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(consentLimit ?? IpcTimeouts.ConsentDaemonLimit);
        ConsentResult result;
        try
        {
            result = await consent.RequestConsentAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new ConsentResult(ConsentOutcome.TimedOut, "Nobody answered the consent dialog in time; the request was closed.");
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("Permission request cancelled: the client went away or the daemon is stopping.");
            throw;
        }

        // The result text is written by Vigil (a path, the portal's error name); a restore token is never part of it.
        if (result.Granted)
        {
            log.LogInformation("Permission request finished: {Outcome}. {Message}", result.Outcome, result.Message);
        }
        else
        {
            log.LogWarning("Permission request finished: {Outcome}. {Message}", result.Outcome, result.Message);
        }

        return result.Granted
            ? new IpcResponse(true, Message: result.Message, Consent: result.Outcome)
            : new IpcResponse(false, result.Message, Consent: result.Outcome);
    }

    public void ResponseSent(IpcRequest request)
    {
        if (request.Command == IpcCommand.Shutdown)
        {
            lifetime.StopApplication();
        }
    }

    private static IpcResponse Fail(string error) => new(false, error);
}
