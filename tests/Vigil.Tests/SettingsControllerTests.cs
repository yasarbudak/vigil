using Vigil.Ipc;
using Vigil.Tray;

namespace Vigil.Tests;

public class SettingsControllerTests
{
    private sealed class FakeStore(SaveResult result) : ISettingsStore
    {
        public int Saves { get; private set; }

        public SettingsLoad Load() => throw new NotSupportedException();

        public SaveResult Save(SettingsForm form)
        {
            Saves++;
            return result;
        }
    }

    private sealed class FakeDaemon(DaemonReply reply) : IDaemonClient
    {
        public List<IpcRequest> Requests { get; } = [];

        public Task<DaemonReply> SendAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(reply);
        }
    }

    private static readonly SettingsForm Form = new("00:04:00", true, false, SettingsForm.AllDays, "00:00", "00:00", true, "30", "MouseNudge");

    private static Task<SaveOutcome> Save(SaveResult store, DaemonReply daemon, out FakeStore fakeStore, out FakeDaemon fakeDaemon)
    {
        fakeStore = new FakeStore(store);
        fakeDaemon = new FakeDaemon(daemon);
        return new SettingsController(fakeStore, fakeDaemon).SaveAsync(Form, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_invalid_form_is_not_sent_to_the_daemon()
    {
        var outcome = await Save(new SaveResult(["Threshold must be greater than zero."]), new DaemonReply(true, true), out _, out var daemon);

        Assert.Equal(SaveOutcomeKind.Invalid, outcome.Kind);
        Assert.False(outcome.Saved);
        Assert.Equal(["Threshold must be greater than zero."], outcome.Messages);
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public async Task A_saved_file_is_followed_by_a_reload_request()
    {
        var outcome = await Save(new SaveResult([]), new DaemonReply(true, true), out var store, out var daemon);

        Assert.Equal(SaveOutcomeKind.Applied, outcome.Kind);
        Assert.True(outcome.Saved);
        Assert.Equal(1, store.Saves);
        Assert.Equal([new IpcRequest(IpcCommand.ReloadConfig)], daemon.Requests);
    }

    [Fact]
    public async Task Keys_that_need_a_restart_are_named()
    {
        var outcome = await Save(new SaveResult([]), new DaemonReply(true, true, RestartRequired: ["Linux.Backend"]), out _, out _);

        Assert.Equal(SaveOutcomeKind.AppliedRestartNeeded, outcome.Kind);
        Assert.Equal(["Linux.Backend"], outcome.RestartRequired);
        Assert.Contains("Linux.Backend", Assert.Single(outcome.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_daemon_the_file_is_saved_and_the_user_is_told_it_applies_at_the_next_start()
    {
        var outcome = await Save(new SaveResult([]), DaemonReply.Unreachable("no socket"), out _, out _);

        Assert.Equal(SaveOutcomeKind.SavedDaemonNotRunning, outcome.Kind);
        Assert.True(outcome.Saved);
        Assert.Contains("apply when it starts", Assert.Single(outcome.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_that_refuses_the_file_has_its_error_shown_as_it_is()
    {
        var outcome = await Save(new SaveResult([]), new DaemonReply(true, false, "Invalid configuration in x: Threshold must be greater than zero."), out _, out _);

        Assert.Equal(SaveOutcomeKind.SavedDaemonRefused, outcome.Kind);
        Assert.Equal(["Invalid configuration in x: Threshold must be greater than zero."], outcome.Messages);
    }
}
