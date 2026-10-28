using Vigil.Activity;
using Vigil.Core.Presence;
using Vigil.Core.Options;
using Vigil.Ipc;

namespace Vigil.Tests;

public class IpcHandlerTests
{
    private static IpcHandler Create(out PresenceEngine engine)
    {
        engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        return new IpcHandler(engine);
    }

    [Fact]
    public void Status_returns_the_engine_status()
    {
        var response = Create(out _).Handle(new IpcRequest(IpcCommand.Status));

        Assert.True(response.Ok);
        Assert.Equal(PresenceState.Watching, response.Status!.State);
    }

    [Fact]
    public void Pause_with_a_duration_pauses()
    {
        var response = Create(out _).Handle(new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(15)));

        Assert.True(response.Ok);
        Assert.Equal(PresenceState.Paused, response.Status!.State);
    }

    [Fact]
    public void Pause_without_a_duration_is_an_error()
    {
        var response = Create(out var engine).Handle(new IpcRequest(IpcCommand.Pause));

        Assert.False(response.Ok);
        Assert.Contains("duration", response.Error, StringComparison.Ordinal);
        Assert.NotEqual(PresenceState.Paused, engine.GetStatus().State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Pause_with_a_non_positive_duration_is_an_error(int seconds)
    {
        var response = Create(out _).Handle(new IpcRequest(IpcCommand.Pause, TimeSpan.FromSeconds(seconds)));

        Assert.False(response.Ok);
    }

    [Fact]
    public void Pause_with_a_duration_that_overflows_the_clock_is_an_error_not_a_crash()
    {
        var response = Create(out var engine).Handle(new IpcRequest(IpcCommand.Pause, TimeSpan.MaxValue));

        Assert.False(response.Ok);
        Assert.NotEqual(PresenceState.Paused, engine.GetStatus().State);
    }

    [Fact]
    public void PauseUntilTomorrow_then_Resume_round_trip()
    {
        var handler = Create(out _);

        Assert.Equal(PresenceState.Paused, handler.Handle(new IpcRequest(IpcCommand.PauseUntilTomorrow)).Status!.State);
        Assert.Equal(PresenceState.Watching, handler.Handle(new IpcRequest(IpcCommand.Resume)).Status!.State);
    }

    [Fact]
    public void Resume_without_a_pause_is_harmless()
    {
        Assert.True(Create(out _).Handle(new IpcRequest(IpcCommand.Resume)).Ok);
    }

    [Fact]
    public void An_unknown_command_is_an_error()
    {
        Assert.False(Create(out _).Handle(new IpcRequest((IpcCommand)99)).Ok);
    }
}
