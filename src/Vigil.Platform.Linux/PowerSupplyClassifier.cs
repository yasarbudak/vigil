namespace Vigil.Platform.Linux;

/// <summary>What one directory of <c>/sys/class/power_supply</c> says. A null attribute was missing or unreadable.</summary>
/// <param name="Type"><c>type</c>: "Battery", "UPS", "Mains", "USB" or "Wireless".</param>
/// <param name="Online"><c>online</c>: 0 offline, 1 online fixed, 2 online programmable; only the supplies that have it.</param>
/// <param name="Status"><c>status</c>: "Unknown", "Charging", "Discharging", "Not charging" or "Full".</param>
/// <param name="Scope"><c>scope</c>: "System" or "Device" (a peripheral's own battery); an old kernel may not have it.</param>
public readonly record struct PowerSupply(string? Type, int? Online, string? Status, string? Scope);

/// <summary>
/// Decides "on battery" from the power supplies the kernel lists. Pure: no file access.
/// <para>
/// Why sysfs and not UPower's <c>OnBattery</c>: it is the kernel's own interface, documented and stable
/// (<c>Documentation/ABI/testing/sysfs-class-power</c>: <c>type</c> "Battery", "UPS", "Mains", "USB", "Wireless";
/// <c>online</c> 0, 1 or 2; <c>status</c> "Unknown", "Charging", "Discharging", "Not charging", "Full"), it needs no running
/// service, no bus call and no extra service name in the privacy statement, and it works the same under every desktop and
/// on a server without UPower. UPower reads the same files; it adds policy for UPS and multiple batteries that Vigil does
/// not need. The <c>scope</c> attribute (drivers/power/supply/power_supply_sysfs.c: "Unknown", "System", "Device") keeps the
/// battery of a wireless mouse from counting as the machine's battery.
/// </para>
/// <para>
/// Rules, mirroring macOS ("the providing power source"): any system supply other than a battery that is online means
/// external power, so not on battery, whatever the battery's status says. Otherwise the machine is on battery when a
/// system battery is discharging, and not when every system battery is charging, full or "not charging" (held by a charge
/// threshold, with a charger present). A machine with no system battery cannot be running on one. Anything else (statuses
/// "Unknown" or unreadable) is unknown.
/// </para>
/// </summary>
public static class PowerSupplyClassifier
{
    /// <returns>True on battery, false on external power or without a battery, null when it cannot be told.</returns>
    public static bool? IsOnBattery(IReadOnlyList<PowerSupply> supplies)
    {
        ArgumentNullException.ThrowIfNull(supplies);

        var system = supplies.Where(s => !string.Equals(s.Scope, "Device", StringComparison.OrdinalIgnoreCase)).ToList();

        if (system.Any(s => IsExternalSource(s.Type) && s.Online is > 0))
        {
            return false;
        }

        var batteries = system.Where(s => string.Equals(s.Type, "Battery", StringComparison.OrdinalIgnoreCase)).ToList();
        if (batteries.Count == 0)
        {
            return false;
        }

        if (batteries.Any(b => string.Equals(b.Status, "Discharging", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return batteries.All(b => b.Status is { } status
            && (string.Equals(status, "Charging", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Not charging", StringComparison.OrdinalIgnoreCase)))
            ? false
            : null;
    }

    private static bool IsExternalSource(string? type) =>
        type is not null && !string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase);
}
