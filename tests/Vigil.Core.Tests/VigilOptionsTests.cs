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
        Assert.Equal(30, options.ActivityLog.RetentionDays);
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
        { "retention zero", o => o.ActivityLog.RetentionDays = 0 },
        { "retention negative", o => o.ActivityLog.RetentionDays = -5 },
        { "retention above maximum", o => o.ActivityLog.RetentionDays = 3651 },
        { "null activity log", o => o.ActivityLog = null! },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_options_are_reported(string name, Action<VigilOptions> mutate)
    {
        var options = new VigilOptions();
        mutate(options);

        Assert.True(options.Validate().Count > 0, name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public void Retention_days_inside_the_range_are_valid(int days)
    {
        var options = new VigilOptions();
        options.ActivityLog.RetentionDays = days;

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Retention_error_names_the_setting_and_the_range()
    {
        var options = new VigilOptions();
        options.ActivityLog.RetentionDays = 0;

        var error = Assert.Single(options.Validate());

        Assert.Contains("ActivityLog.RetentionDays", error, StringComparison.Ordinal);
        Assert.Contains("1", error, StringComparison.Ordinal);
        Assert.Contains("3650", error, StringComparison.Ordinal);
    }
}
