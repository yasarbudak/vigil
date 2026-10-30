namespace Vigil.Platform.Windows;

/// <summary>Interprets SYSTEM_POWER_STATUS.ACLineStatus. Pure: no Win32 calls.</summary>
internal static class PowerStatusClassifier
{
    // ACLineStatus: 0 = Offline, 1 = Online, 255 = Unknown status
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status
    private const byte Offline = 0;
    private const byte Online = 1;

    /// <returns>True when running on DC power (offline from AC), false on AC, null for 255 and any undocumented value.</returns>
    public static bool? IsOnBattery(byte acLineStatus) => acLineStatus switch
    {
        Offline => true,
        Online => false,
        _ => null,
    };
}
