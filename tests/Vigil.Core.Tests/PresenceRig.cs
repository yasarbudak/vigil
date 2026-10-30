using Vigil.Core.Options;
using Vigil.Core.Presence;

namespace Vigil.Core.Tests;

/// <summary>Wires the engine and cycle to the simulated machine. Monday 2026-03-09 10:00 UTC unless told otherwise.</summary>
internal sealed class PresenceRig
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 3, 9, 10, 0, 0, TimeSpan.Zero);

    public PresenceRig(Action<VigilOptions>? configure = null, DateTimeOffset? start = null, DailyTotalsSeed? seed = null, TimeZoneInfo? zone = null)
    {
        Options = new VigilOptions();
        configure?.Invoke(Options);

        Time = new ManualTimeProvider(start ?? DefaultStart, zone);
        Input = new FakeInput(Time);
        Session = new FakeSession();
        Log = new FakeActivityLog();
        if (seed is not null)
        {
            Log.Seed = new(seed.Injections, seed.Covered);
        }

        Engine = new PresenceEngine(Options, Time, Log);
        Cycle = new PresenceCycle(Engine, Input, Input, Session, Session, Time);
    }

    public VigilOptions Options { get; }

    public ManualTimeProvider Time { get; }

    public FakeInput Input { get; }

    public FakeSession Session { get; }

    public FakeActivityLog Log { get; }

    public PresenceEngine Engine { get; }

    public PresenceCycle Cycle { get; }

    public PresenceStatus Status => Engine.GetStatus();

    /// <summary>Lets <paramref name="elapsed"/> pass, then runs one poll, including the verification wait if one starts.</summary>
    public async Task PollAfterAsync(TimeSpan elapsed)
    {
        Time.Advance(elapsed);
        var poll = Cycle.RunOnceAsync(CancellationToken.None);
        if (!poll.IsCompleted)
        {
            Time.Advance(Options.VerifyDelay);
        }

        await poll;
    }

    /// <summary>Polls every <see cref="VigilOptions.PollInterval"/> until <paramref name="total"/> has passed.</summary>
    public async Task RunForAsync(TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += Options.PollInterval)
        {
            await PollAfterAsync(Options.PollInterval);
        }
    }
}

internal sealed record DailyTotalsSeed(int Injections, TimeSpan Covered);
