using Vigil.Platform;

namespace Vigil.Tests;

public class LockDetectionMonitorTests
{
    [Fact]
    public void Staying_known_or_staying_unknown_reports_nothing()
    {
        var known = new LockDetectionMonitor(initiallyKnown: true);
        var unknown = new LockDetectionMonitor(initiallyKnown: false);

        for (var poll = 0; poll < 3; poll++)
        {
            Assert.Equal(LockDetectionChange.None, known.Observe(true));
            Assert.Equal(LockDetectionChange.None, unknown.Observe(false));
        }
    }

    [Fact]
    public void Losing_detection_is_reported_once_not_on_every_poll()
    {
        var monitor = new LockDetectionMonitor(initiallyKnown: true);

        Assert.Equal(LockDetectionChange.Lost, monitor.Observe(false));
        Assert.Equal(LockDetectionChange.None, monitor.Observe(false));
        Assert.Equal(LockDetectionChange.None, monitor.Observe(false));
    }

    [Fact]
    public void Recovery_is_reported_once_and_a_second_loss_is_reported_again()
    {
        var monitor = new LockDetectionMonitor(initiallyKnown: false);

        Assert.Equal(LockDetectionChange.Restored, monitor.Observe(true));
        Assert.Equal(LockDetectionChange.None, monitor.Observe(true));
        Assert.Equal(LockDetectionChange.Lost, monitor.Observe(false));
        Assert.Equal(LockDetectionChange.Restored, monitor.Observe(true));
    }
}
