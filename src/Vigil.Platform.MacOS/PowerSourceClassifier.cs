namespace Vigil.Platform.MacOS;

/// <summary>Interprets the power source type returned by IOPSGetProvidingPowerSourceType.</summary>
internal static class PowerSourceClassifier
{
    // IOPowerSources.h: kIOPMBatteryPowerKey "Battery Power", kIOPMACPowerKey "AC Power", kIOPMUPSPowerKey "UPS Power"
    private const string BatteryPowerKey = "Battery Power";
    private const string AcPowerKey = "AC Power";
    private const string UpsPowerKey = "UPS Power";

    /// <returns>True on battery, false on AC or UPS, null when the type is unreadable or unrecognised.</returns>
    public static bool? IsOnBattery(string? sourceType) => sourceType switch
    {
        BatteryPowerKey => true,
        AcPowerKey or UpsPowerKey => false,
        _ => null,
    };
}
