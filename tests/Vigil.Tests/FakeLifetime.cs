using Microsoft.Extensions.Hosting;

namespace Vigil.Tests;

/// <summary>Records whether the daemon was asked to stop, without a host.</summary>
internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public int StopCalls { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => StopCalls++;
}
