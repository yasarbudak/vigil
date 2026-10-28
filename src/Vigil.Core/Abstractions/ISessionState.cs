namespace Vigil.Core.Abstractions;

/// <summary>Session facts that can pause Vigil. <c>null</c> means the platform cannot tell.</summary>
public interface ISessionState
{
    bool? IsScreenLocked();

    bool? IsOnBattery();
}
