using Vigil.Core.Options;

namespace Vigil.Core.Tests;

public class VigilOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_match_the_documented_values()
    {
        var options = new VigilOptions();

        Assert.Empty(options.Validate());
        Assert.Equal(TimeSpan.FromMinutes(4), options.Threshold);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PollInterval);
        Assert.True(options.PauseOnLock);
        Assert.False(options.PauseOnBattery);
        Assert.True(options.ActivityLog.Enabled);
        Assert.Null(options.Schedule.ActiveDays);
    }

    public static TheoryData<string, Action<VigilOptions>> Invalid => new()
    {
        { "zero threshold", o => o.Threshold = TimeSpan.Zero },
        { "negative threshold", o => o.Threshold = TimeSpan.FromSeconds(-1) },
        { "zero poll", o => o.PollInterval = TimeSpan.Zero },
        { "poll equals threshold", o => o.PollInterval = o.Threshold },
        { "negative verify delay", o => o.VerifyDelay = TimeSpan.FromMilliseconds(-1) },
        { "verify delay not shorter than poll", o => o.VerifyDelay = o.PollInterval },
        { "retry shorter than poll", o => o.DegradedRetryInterval = TimeSpan.FromSeconds(1) },
        { "empty active days", o => o.Schedule.ActiveDays = [] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_options_are_reported(string name, Action<VigilOptions> mutate)
    {
        var options = new VigilOptions();
        mutate(options);

        Assert.True(options.Validate().Count > 0, name);
    }
}
