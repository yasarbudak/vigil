using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// Input through the xdg-desktop-portal RemoteDesktop interface: the Wayland way, with a consent dialog that only
/// <see cref="RequestAccess"/> (that is, <c>vigil permissions request</c>) may trigger. See <see cref="PortalSessionManager"/>.
/// </summary>
internal sealed class PortalInputBackend(PortalSessionManager manager) : IInputBackend
{
    private string _lastMessage = string.Empty;

    public string Name => "desktop portal (RemoteDesktop)";

    public string NotGrantedHint => string.IsNullOrEmpty(_lastMessage)
        ? "The portal consent was not given. Run 'vigil permissions request' again and allow the dialog."
        : _lastMessage;

    public InjectionResult Inject() => manager.Inject();

    public bool CanPostEvents() => manager.CanPostEvents();

    /// <summary>May show the portal's consent dialog and waits for the answer. Only the request command calls this.</summary>
    public bool RequestAccess()
    {
        var result = manager.RequestConsent();
        _lastMessage = result.Message;
        return result.Granted;
    }

    public IReadOnlyList<LinuxDetail> Diagnose() => manager.Diagnose();

    public void Dispose() => manager.Dispose();
}
