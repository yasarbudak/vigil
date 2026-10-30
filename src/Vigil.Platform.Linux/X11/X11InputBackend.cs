using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.X11;

/// <summary>
/// Posts one invisible event through the XTest extension (libXtst) using the configured strategy. XTest events are
/// processed by the X server like input from a device, so they reset the idle counter the same way. XTest needs no
/// permission, so the probe says "granted" whenever the extension can be used, and "request" has nothing to ask.
/// https://gitlab.freedesktop.org/xorg/lib/libxtst/-/blob/master/include/X11/extensions/XTest.h
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11InputBackend(LinuxOptions options, X11Session session) : IInputBackend
{
    public string Name => "X11 (XTest)";

    public string NotGrantedHint
    {
        get
        {
            session.CanPost(out var problem);
            return problem ?? "XTest cannot be used.";
        }
    }

    public InjectionResult Inject() => options.Strategy switch
    {
        InjectionStrategy.MouseNudge => session.NudgeMouse(),
        InjectionStrategy.F15KeyUp => session.ReleaseF15(),
        _ => InjectionResult.Failure($"Unknown injection strategy '{options.Strategy}'."),
    };

    public bool CanPostEvents() => session.CanPost(out _);

    /// <summary>X11 has no consent to ask for; this only reports whether XTest is usable.</summary>
    public bool RequestAccess() => session.CanPost(out _);

    public IReadOnlyList<LinuxDetail> Diagnose()
    {
        var usable = session.CanPost(out var problem);
        return [new LinuxDetail("XTest:", usable ? "available" : problem ?? "not available")];
    }

    public void Dispose() => session.Dispose();
}
