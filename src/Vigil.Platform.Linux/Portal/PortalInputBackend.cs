using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// Input through the xdg-desktop-portal RemoteDesktop interface: the Wayland way, with a consent dialog that only
/// <see cref="RequestConsentAsync"/> may trigger, in the daemon, in answer to <c>vigil permissions request</c>. See <see cref="PortalSessionManager"/>.
/// </summary>
internal sealed class PortalInputBackend(PortalSessionManager manager) : IInputBackend, IConsentRequester
{
    public string Name => "desktop portal (RemoteDesktop)";

    public string NotGrantedHint =>
        "No portal consent is saved. Start the daemon ('vigil install' or 'vigil run'), then run 'vigil permissions request' and allow the dialog.";

    public InjectionResult Inject() => manager.Inject();

    public bool CanPostEvents() => manager.CanPostEvents();

    /// <summary>
    /// Never asks: the portal ties the consent to the identity of the process that asked, so only the daemon may ask
    /// (<see cref="RequestConsentAsync"/>). A process that calls this only learns whether a consent is there.
    /// </summary>
    public bool RequestAccess() => manager.CanPostEvents();

    /// <summary>May show the portal's consent dialog and waits for the answer. Only the daemon's IPC handler calls this.</summary>
    public Task<ConsentResult> RequestConsentAsync(CancellationToken cancellationToken) => manager.RequestConsentAsync(cancellationToken);

    public IReadOnlyList<LinuxDetail> Diagnose() => manager.Diagnose();

    public void Dispose() => manager.Dispose();
}
