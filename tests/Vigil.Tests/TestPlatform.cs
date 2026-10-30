using Vigil.Core.Abstractions;
using Vigil.Platform;

namespace Vigil.Tests;

/// <summary>A fake platform. It never posts real input; it only counts the injections requested.</summary>
internal sealed class TestPlatform : IIdleSource, IInputInjector, ISessionState, IPermissionProbe
{
    public TimeSpan Idle { get; set; } = TimeSpan.Zero;

    public bool? Locked { get; set; } = false;

    public int InjectCalls { get; private set; }

    public TimeSpan GetIdleTime() => Idle;

    public InjectionResult Inject()
    {
        InjectCalls++;
        Idle = TimeSpan.Zero;
        return InjectionResult.Success;
    }

    public bool? IsScreenLocked() => Locked;

    public bool? IsOnBattery() => false;

    public bool CanPostEvents() => true;

    public Func<PlatformOptions, PlatformServices> Factory => _ => new PlatformServices("test", this, this, this, this, null);
}
