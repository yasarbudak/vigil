using Vigil.Platform.Linux;

namespace Vigil.Tests;

public class LinuxPowerSupplyTests
{
    private static PowerSupply Battery(string? status, string? scope = null) => new("Battery", null, status, scope);

    private static PowerSupply Mains(int? online, string type = "Mains") => new(type, online, null, null);

    [Fact]
    public void A_laptop_on_battery_is_on_battery()
    {
        Assert.True(PowerSupplyClassifier.IsOnBattery([Mains(0), Battery("Discharging")]));
    }

    [Theory]
    [InlineData("Charging")]
    [InlineData("Full")]
    [InlineData("Not charging")]
    [InlineData("Discharging")] // Calibration or a fuel gauge lagging behind: the charger wins, as macOS reports the providing source.
    public void A_laptop_with_the_charger_online_is_not_on_battery(string status)
    {
        Assert.False(PowerSupplyClassifier.IsOnBattery([Mains(1), Battery(status)]));
    }

    [Theory]
    [InlineData("USB")]
    [InlineData("Wireless")]
    [InlineData("UPS")]
    public void Any_online_supply_that_is_not_a_battery_means_external_power(string type)
    {
        Assert.False(PowerSupplyClassifier.IsOnBattery([Mains(2, type), Battery("Discharging")]));
    }

    [Fact]
    public void An_offline_charger_does_not_count()
    {
        Assert.True(PowerSupplyClassifier.IsOnBattery([Mains(0, "USB"), Mains(0), Battery("Discharging")]));
    }

    [Fact]
    public void The_battery_of_a_peripheral_is_not_the_machines_battery()
    {
        // A wireless mouse reports type Battery, scope Device; the desktop is on mains power.
        Assert.False(PowerSupplyClassifier.IsOnBattery([Battery("Discharging", scope: "Device")]));
        Assert.True(PowerSupplyClassifier.IsOnBattery([Mains(0), Battery("Discharging", "System"), Battery("Full", "Device")]));
    }

    [Fact]
    public void A_machine_without_a_battery_cannot_be_on_battery()
    {
        Assert.False(PowerSupplyClassifier.IsOnBattery([]));
        Assert.False(PowerSupplyClassifier.IsOnBattery([Mains(1)]));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData(null)]
    [InlineData("something new")]
    public void A_battery_whose_state_is_not_known_gives_unknown(string? status)
    {
        Assert.Null(PowerSupplyClassifier.IsOnBattery([Mains(0), Battery(status)]));
    }

    [Fact]
    public void One_discharging_battery_among_several_is_enough()
    {
        Assert.True(PowerSupplyClassifier.IsOnBattery([Mains(0), Battery("Full"), Battery("Discharging")]));
    }

    [Fact]
    public void The_type_comparison_ignores_case()
    {
        Assert.True(PowerSupplyClassifier.IsOnBattery([new PowerSupply("mains", 0, null, null), new PowerSupply("battery", null, "discharging", "system")]));
    }
}

public sealed class LinuxPowerSupplyReaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");

    public void Dispose() => _root.Delete(recursive: true);

    private void Supply(string name, params (string File, string Content)[] attributes)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, name));
        foreach (var (file, content) in attributes)
        {
            File.WriteAllText(Path.Combine(directory.FullName, file), content);
        }
    }

    [Fact]
    public void Reads_the_four_attributes_of_each_supply_and_trims_the_newline()
    {
        Supply("AC", ("type", "Mains\n"), ("online", "1\n"));
        Supply("BAT0", ("type", "Battery\n"), ("status", "Discharging\n"), ("scope", "System\n"), ("capacity", "55\n"));

        var supplies = new PowerSupplyReader(_root.FullName).Read()!;

        Assert.Equal(2, supplies.Count);
        Assert.Contains(new PowerSupply("Mains", 1, null, null), supplies);
        Assert.Contains(new PowerSupply("Battery", null, "Discharging", "System"), supplies);
    }

    [Fact]
    public void A_missing_attribute_is_null_and_a_non_number_online_is_null()
    {
        Supply("odd", ("type", "Mains"), ("online", "maybe"));

        Assert.Equal([new PowerSupply("Mains", null, null, null)], new PowerSupplyReader(_root.FullName).Read());
    }

    [Fact]
    public void An_empty_directory_is_a_machine_without_supplies()
    {
        Assert.Empty(new PowerSupplyReader(_root.FullName).Read()!);
    }

    [Fact]
    public void A_missing_directory_is_unknown_not_empty()
    {
        Assert.Null(new PowerSupplyReader(Path.Combine(_root.FullName, "nope")).Read());
    }
}
