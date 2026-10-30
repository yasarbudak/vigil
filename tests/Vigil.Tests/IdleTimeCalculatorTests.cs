using Vigil.Platform.Windows;

namespace Vigil.Tests;

public class IdleTimeCalculatorTests
{
    [Theory]
    [InlineData(10_000u, 4_000u, 6_000)]
    [InlineData(10_000u, 10_000u, 0)]
    [InlineData(0u, 0u, 0)]
    [InlineData(300_000u, 60_000u, 240_000)]
    public void Idle_time_is_the_difference_of_the_two_tick_counts_when_no_wrap_is_involved(uint now, uint lastInput, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), IdleTimeCalculator.Elapsed(now, lastInput));
    }

    [Fact]
    public void The_difference_is_right_across_the_49_7_day_wrap_of_the_tick_count()
    {
        // The last input was 900 ms before the counter wrapped to zero, and the counter now reads 100: 1000 ms idle.
        var idle = IdleTimeCalculator.Elapsed(currentTick: 100, lastInputTick: uint.MaxValue - 899);

        Assert.Equal(TimeSpan.FromMilliseconds(1000), idle);
    }

    [Fact]
    public void A_wrap_exactly_at_the_boundary_counts_the_millisecond_before_it()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1), IdleTimeCalculator.Elapsed(currentTick: 0, lastInputTick: uint.MaxValue));
        Assert.Equal(TimeSpan.FromMilliseconds(2), IdleTimeCalculator.Elapsed(currentTick: 1, lastInputTick: uint.MaxValue));
    }

    [Theory]
    [InlineData(1_000u, 1_005u)] // an event raised by SendInput supplies its own, slightly later tick count
    [InlineData(0u, 1u)]
    [InlineData(uint.MaxValue - 4, 1u)] // ahead of the counter by a few ms right across a wrap (6 ms)
    public void A_last_input_slightly_in_the_future_is_zero_idle_not_49_days(uint now, uint lastInput)
    {
        Assert.Equal(TimeSpan.Zero, IdleTimeCalculator.Elapsed(now, lastInput));
    }

    [Fact]
    public void Idle_times_up_to_2_pow_31_milliseconds_are_reported_and_longer_ones_read_as_zero()
    {
        var longest = IdleTimeCalculator.Elapsed(currentTick: int.MaxValue, lastInputTick: 0);
        var beyond = IdleTimeCalculator.Elapsed(currentTick: (uint)int.MaxValue + 1, lastInputTick: 0);

        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), longest); // 24.8 days
        Assert.Equal(TimeSpan.Zero, beyond);
    }
}
