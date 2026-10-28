namespace Vigil.Core.Tests;

public class DurationParserTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("1h", 3600)]
    [InlineData("1h30m", 5400)]
    [InlineData("2D", 172800)]
    [InlineData(" 10m ", 600)]
    public void Valid_forms_are_parsed(string text, int seconds)
    {
        Assert.True(DurationParser.TryParse(text, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("15")]
    [InlineData("m")]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("5x")]
    [InlineData("1h 30m")]
    [InlineData("99999999999m")]
    [InlineData("2147483647d")]
    public void Invalid_forms_are_rejected(string? text)
    {
        Assert.False(DurationParser.TryParse(text, out _));
    }
}
