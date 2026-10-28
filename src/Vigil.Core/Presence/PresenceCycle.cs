using Vigil.Core.Abstractions;
using Vigil.Core.Options;

namespace Vigil.Core.Presence;

/// <summary>
/// One poll step: observe, decide, and when due inject, wait, re-read idle time and report the result.
/// The host calls <see cref="RunOnceAsync"/> every <see cref="VigilOptions.PollInterval"/>.
/// </summary>
public sealed class PresenceCycle
{
    private readonly PresenceEngine _engine;
    private readonly IIdleSource _idle;
    private readonly IInputInjector _injector;
    private readonly ISessionState _session;
    private readonly IPermissionProbe _permission;
    private readonly TimeProvider _time;
    private readonly TimeSpan _verifyDelay;

    public PresenceCycle(
        PresenceEngine engine,
        IIdleSource idle,
        IInputInjector injector,
        ISessionState session,
        IPermissionProbe permission,
        TimeProvider time,
        VigilOptions options)
    {
        _engine = engine;
        _idle = idle;
        _injector = injector;
        _session = session;
        _permission = permission;
        _time = time;
        _verifyDelay = options.VerifyDelay;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var observation = new PresenceObservation(
            _idle.GetIdleTime(),
            _session.IsScreenLocked(),
            _session.IsOnBattery(),
            _permission.CanPostEvents());

        if (!_engine.Evaluate(observation).ShouldInject)
        {
            return;
        }

        var result = _injector.Inject();
        if (result.Posted)
        {
            await Task.Delay(_verifyDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        _engine.RecordInjection(result, _idle.GetIdleTime());
    }
}
