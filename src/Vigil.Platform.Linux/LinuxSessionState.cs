using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux;

/// <summary>
/// Lock state from logind (<see cref="LogindSessionLock"/>) and battery state from sysfs (<see cref="PowerSupplyClassifier"/>).
/// Both return null when they cannot be told, as on macOS and Windows: Vigil then keeps injecting and says so.
/// </summary>
internal sealed class LinuxSessionState(LogindSessionLock lockHint, PowerSupplyReader powerSupplies) : ISessionState
{
    public bool? IsScreenLocked() => lockHint.IsLocked();

    public bool? IsOnBattery() => powerSupplies.Read() is { } supplies ? PowerSupplyClassifier.IsOnBattery(supplies) : null;
}
