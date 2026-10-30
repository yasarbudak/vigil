using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.Uinput;

/// <summary>
/// The opt-in fallback: posts through a virtual device on /dev/uinput. Vigil never installs the udev rule or the group that
/// make the device writable and never needs root: the user sets that up on their own and selects this backend with
/// <c>Linux.Backend</c> (README, "Linux: uinput"). There is no prompt, so "request" only re-checks access.
/// The virtual device exists from the first injection until the daemon exits.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UinputInputBackend(LinuxOptions options, Func<string, UinputOpenResult>? opener = null) : IInputBackend
{
    private readonly object _gate = new();
    private readonly Func<string, UinputOpenResult> _opener = opener ?? UinputAccess.TryOpen;
    private UinputDevice? _device;

    public string Name => "uinput (/dev/uinput)";

    public string NotGrantedHint
    {
        get
        {
            UinputAccess.Probe(_opener, out var problem);
            return problem ?? "uinput can be used.";
        }
    }

    public bool CanPostEvents()
    {
        lock (_gate)
        {
            return _device is not null || UinputAccess.Probe(_opener, out _) is not null;
        }
    }

    public bool RequestAccess() => CanPostEvents();

    public InjectionResult Inject()
    {
        lock (_gate)
        {
            try
            {
                if (_device is null)
                {
                    var path = UinputAccess.Probe(_opener, out var problem) ?? throw new LinuxUnavailableException(problem!);
                    _device = UinputDevice.Create(path, options.Strategy);
                    Thread.Sleep(UinputDevice.SettleDelay);
                }

                var result = _device.Write(options.Strategy == InjectionStrategy.MouseNudge ? UinputProtocol.MouseNudge() : UinputProtocol.F15Tap());
                if (!result.Posted)
                {
                    DropDevice(); // Created again at the next attempt.
                }

                return result;
            }
            catch (LinuxUnavailableException ex)
            {
                DropDevice();
                return InjectionResult.Failure(ex.Message);
            }
        }
    }

    public IReadOnlyList<LinuxDetail> Diagnose()
    {
        lock (_gate)
        {
            if (_device is not null)
            {
                return [new LinuxDetail("uinput:", "virtual device created")];
            }

            var path = UinputAccess.Probe(_opener, out var problem);
            return [new LinuxDetail("uinput:", path is null ? problem ?? "not available" : $"{path} is writable")];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DropDevice();
        }
    }

    private void DropDevice()
    {
        _device?.Dispose();
        _device = null;
    }
}
