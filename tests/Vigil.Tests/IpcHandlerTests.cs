using Vigil.Activity;
using Vigil.Core.Presence;
using Vigil.Core.Options;
using Vigil.Ipc;

namespace Vigil.Tests;

public class IpcHandlerTests
{
    private static IpcHandler Create(out PresenceEngine engine) => Create(out engine, out _);

    private static IpcHandler Create(out PresenceEngine engine, out FakeLifetime lifetime)
    {
        engine = new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog());
        lifetime = new FakeLifetime();
        return new IpcHandler(engine, lifetime);
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

    [Fact]
    public void Shutdown_is_acknowledged_but_the_daemon_stops_only_after_the_response_was_sent()
    {
        var handler = Create(out _, out var lifetime);
        var request = new IpcRequest(IpcCommand.Shutdown);

        var response = handler.Handle(request);

        Assert.True(response.Ok);
        Assert.Equal(0, lifetime.StopCalls); // The client must get its answer before the connection can be cut.

        handler.ResponseSent(request);

        Assert.Equal(1, lifetime.StopCalls);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("Resume")]
    public void Other_commands_never_stop_the_daemon(string command)
    {
        var handler = Create(out _, out var lifetime);
        var request = new IpcRequest(Enum.Parse<IpcCommand>(command));

        handler.Handle(request);
        handler.ResponseSent(request);

        Assert.Equal(0, lifetime.StopCalls);
    }
}
