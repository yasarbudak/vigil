using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Ipc;
using Vigil.Platform;

namespace Vigil.Commands;

internal static class PermissionsCommand
{
    /// <summary>
    /// The only code path that may make the OS show a permission prompt. Where the OS ties the consent to the process that
    /// asked (Linux, desktop portal) the running daemon asks for it, over IPC, because the daemon is the process that has to
    /// use it; this process never asks in that case. Elsewhere (macOS) it asks in this process.
    /// </summary>
    /// <param name="platformFactory">Replaces the OS implementations; used by tests so no real permission prompt can appear.</param>
    public static async Task<int> RequestAsync(
        VigilPaths paths,
        TextWriter output,
        TextWriter error,
        Func<PlatformOptions, PlatformServices>? platformFactory = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var platform = (platformFactory ?? PlatformServices.Create)(VigilConfiguration.Load(paths.ConfigPath).Platform with { DataDirectory = paths.DataDirectory });
            if (platform.DaemonConsent is not null)
            {
                return await RequestThroughDaemonAsync(paths, output, error, cancellationToken).ConfigureAwait(false);
            }

            if (platform.PermissionRequester is null)
            {
                // A platform without a requester either needs no permission (Windows) or has none Vigil can ask for.
                if (platform.Permissions.CanPostEvents())
                {
                    output.WriteLine($"Nothing to request: {platform.Name} needs no permission to post input events.");
                    return 0;
                }

                error.WriteLine("vigil: this platform has no permission to request.");
                return 1;
            }

            var granted = platform.PermissionRequester.RequestAccess();
            output.WriteLine(granted
                ? "Permission to post input events is granted."
                : platform.PermissionNotGrantedHint?.Invoke() ?? "Permission not granted.");
            return granted ? 0 : 1;
        }
        catch (PlatformNotSupportedException ex)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 1;
        }
        catch (ConfigurationException ex)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Sends <see cref="IpcCommand.RequestPermission"/> and waits for the user's answer to the dialog the daemon shows (up to
    /// <see cref="IpcTimeouts.Consent"/>). Without a daemon it stops: asking from this process would give the consent to an
    /// identity the daemon does not have.
    /// </summary>
    private static async Task<int> RequestThroughDaemonAsync(VigilPaths paths, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            output.WriteLine($"Asking the running daemon to request the permission. Answer the dialog of your desktop; this waits up to {(int)IpcTimeouts.Consent.TotalMinutes} minutes.");
            var response = await IpcClient.SendAsync(paths.SocketPath, new IpcRequest(IpcCommand.RequestPermission), cancellationToken).ConfigureAwait(false);
            if (response.Ok)
            {
                output.WriteLine("Permission to post input events is granted.");
                if (response.Message is { Length: > 0 } message)
                {
                    output.WriteLine(message);
                }

                return 0;
            }

            error.WriteLine($"vigil: permission request {Describe(response.Consent)}: {response.Error ?? "The daemon reported an error."}");
            return 1;
        }
        catch (DaemonNotRunningException)
        {
            error.WriteLine("vigil: the Vigil daemon is not running. The daemon has to ask for the permission itself, so start it first with 'vigil install' or 'vigil run', then run 'vigil permissions request' again.");
            return 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error.WriteLine($"vigil: permission request timed out: the daemon gave no answer within {(int)IpcTimeouts.Consent.TotalMinutes} minutes.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("vigil: permission request cancelled here. The daemon notices that and closes its dialog.");
            return 1;
        }
        catch (IOException ex)
        {
            error.WriteLine($"vigil: {ex.Message}");
            return 1;
        }
    }

    private static string Describe(ConsentOutcome? outcome) => outcome switch
    {
        ConsentOutcome.Denied => "denied",
        ConsentOutcome.Cancelled => "cancelled",
        ConsentOutcome.TimedOut => "timed out",
        ConsentOutcome.InProgress => "already in progress",
        ConsentOutcome.Failed => "failed",
        _ => "refused",
    };
}
