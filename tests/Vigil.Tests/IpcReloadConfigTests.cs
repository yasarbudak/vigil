using System.Text.Json;
using Vigil.Activity;
using Vigil.Configuration;
using Vigil.Core.Options;
using Vigil.Core.Presence;
using Vigil.Ipc;

namespace Vigil.Tests;

public class IpcReloadConfigTests
{
    private sealed class FakeReloader(ReloadResult result) : IConfigReloader
    {
        public int Calls { get; private set; }

        public ReloadResult Reload()
        {
            Calls++;
            return result;
        }
    }

    private static IpcHandler Create(IConfigReloader? reloader) =>
        new(new PresenceEngine(new VigilOptions(), TimeProvider.System, new NullActivityLog()), new FakeLifetime(), reloader: reloader);

    [Fact]
    public void A_reload_that_worked_answers_with_the_status()
    {
        var reloader = new FakeReloader(new ReloadResult(null, []));

        var response = Create(reloader).Handle(new IpcRequest(IpcCommand.ReloadConfig));

        Assert.True(response.Ok);
        Assert.NotNull(response.Status);
        Assert.Null(response.RestartRequired);
        Assert.Equal(1, reloader.Calls);
    }

    [Fact]
    public void Keys_that_need_a_restart_are_reported()
    {
        var response = Create(new FakeReloader(new ReloadResult(null, ["Linux.Backend"]))).Handle(new IpcRequest(IpcCommand.ReloadConfig));

        Assert.True(response.Ok);
        Assert.Equal(["Linux.Backend"], response.RestartRequired);
    }

    [Fact]
    public void A_refused_reload_returns_the_error_and_no_status()
    {
        var response = Create(new FakeReloader(ReloadResult.Failed("Invalid configuration in x: Threshold must be greater than zero."))).Handle(new IpcRequest(IpcCommand.ReloadConfig));

        Assert.False(response.Ok);
        Assert.Contains("Threshold must be greater than zero", response.Error, StringComparison.Ordinal);
        Assert.Null(response.Status);
    }

    [Fact]
    public void Without_a_reloader_the_command_is_refused()
    {
        var response = Create(null).Handle(new IpcRequest(IpcCommand.ReloadConfig));

        Assert.False(response.Ok);
        Assert.Contains("cannot reload", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_commands_never_reload()
    {
        var reloader = new FakeReloader(new ReloadResult(null, []));
        var handler = Create(reloader);

        handler.Handle(new IpcRequest(IpcCommand.Status));
        handler.Handle(new IpcRequest(IpcCommand.Resume));

        Assert.Equal(0, reloader.Calls);
    }

    [Fact]
    public void The_command_and_the_restart_list_survive_the_wire_format()
    {
        var request = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new IpcRequest(IpcCommand.ReloadConfig), VigilJsonContext.Default.IpcRequest),
            VigilJsonContext.Default.IpcRequest);
        var response = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new IpcResponse(true, RestartRequired: ["Linux.Backend", "Linux.Strategy"]), VigilJsonContext.Default.IpcResponse),
            VigilJsonContext.Default.IpcResponse);

        Assert.Equal(IpcCommand.ReloadConfig, request!.Command);
        Assert.Equal(["Linux.Backend", "Linux.Strategy"], response!.RestartRequired);
    }

    [Fact]
    public void A_response_without_a_restart_list_does_not_carry_the_field()
    {
        var json = JsonSerializer.Serialize(new IpcResponse(true), VigilJsonContext.Default.IpcResponse);

        Assert.DoesNotContain("restartRequired", json, StringComparison.Ordinal);
    }
}
