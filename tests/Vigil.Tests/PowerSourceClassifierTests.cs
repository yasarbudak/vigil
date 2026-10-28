using Vigil.Platform.MacOS;

namespace Vigil.Tests;

public class PowerSourceClassifierTests
{
    [Theory]
    [InlineData("Battery Power", true)]
    [InlineData("AC Power", false)]
    [InlineData("UPS Power", false)]
    [InlineData("Something New", null)]
    [InlineData(null, null)]
    public void Power_source_is_classified_and_unreadable_values_are_unknown(string? sourceType, bool? expected)
    {
        Assert.Equal(expected, PowerSourceClassifier.IsOnBattery(sourceType));
    }
}
