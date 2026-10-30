using Vigil.Core.Abstractions;
using Vigil.Platform.Linux;
using Vigil.Platform.Linux.DBus;
using Vigil.Platform.Linux.Portal;

namespace Vigil.Tests;

public sealed class LinuxPortalTokenStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");
    private string Path => System.IO.Path.Combine(_root.FullName, "portal-restore-token");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("abc_DEF-123", true)]
    [InlineData("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0", true)] // the UUID form older portals return
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("new\nline", false)]
    [InlineData("a/b", false)]
    [InlineData("é", false)]
    public void Only_the_characters_of_a_portal_token_are_valid(string? token, bool valid)
    {
        Assert.Equal(valid, PortalTokenStore.IsValid(token));
    }

    [Fact]
    public void A_token_is_written_read_replaced_and_deleted()
    {
        var store = new PortalTokenStore(Path);
        Assert.False(store.Exists());
        Assert.Null(store.Read());

        store.Write("first");
        Assert.True(store.Exists());
        Assert.Equal("first", store.Read());

        store.Write("second");
        Assert.Equal("second", store.Read());

        Assert.True(store.Delete());
        Assert.False(store.Delete());
        Assert.Null(store.Read());
    }

    [Fact]
    public void The_file_is_private_to_the_user_and_leaves_no_temporary_file()
    {
        var store = new PortalTokenStore(Path);

        store.Write("first");
        store.Write("second");

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path));
        }

        Assert.Equal([Path], Directory.GetFileSystemEntries(_root.FullName)); // no .tmp left behind
    }

    [Fact]
    public void A_missing_directory_is_created_private()
    {
        var nested = System.IO.Path.Combine(_root.FullName, "data", "vigil", "portal-restore-token");

        new PortalTokenStore(nested).Write("tok");

        Assert.Equal("tok", new PortalTokenStore(nested).Read());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(System.IO.Path.GetDirectoryName(nested)!));
        }
    }

    [Fact]
    public void A_file_with_something_else_in_it_reads_as_no_token_and_an_invalid_token_is_never_written()
    {
        File.WriteAllText(Path, "not a token\nsecond line");
        var store = new PortalTokenStore(Path);

        Assert.True(store.Exists());
        Assert.Null(store.Read());
        Assert.Throws<ArgumentException>(() => store.Write("bad token"));
        Assert.Equal("not a token\nsecond line", File.ReadAllText(Path)); // untouched
    }
}

public sealed class LinuxPortalSessionManagerTests : IDisposable
{
    private static readonly PortalTimeouts Quick = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");
    private readonly FakePortalConnection _portal = new();
    private readonly PortalTokenStore _tokens;

    public LinuxPortalSessionManagerTests() => _tokens = new PortalTokenStore(Path.Combine(_root.FullName, "portal-restore-token"));

    public void Dispose() => _root.Delete(recursive: true);

    private FakeConnector? _connector;

    private PortalSessionManager Manager(InjectionStrategy strategy = InjectionStrategy.MouseNudge)
    {
        _connector = new FakeConnector(_ => _portal);
        return new PortalSessionManager(new PortalClient(_connector, Quick), _tokens, new LinuxOptions { Strategy = strategy });
    }

    [Fact]
    public void Without_a_saved_token_there_is_no_permission_and_nothing_touches_the_bus()
    {
        using var manager = Manager();

        Assert.False(manager.CanPostEvents());
        var result = manager.Inject();

        Assert.False(result.Posted);
        Assert.Contains("vigil permissions request", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, _connector!.Connects);
    }

    [Fact]
    public void A_saved_token_is_permission_enough_to_try_and_the_probe_never_connects()
    {
        _tokens.Write("saved");
        using var manager = Manager();

        Assert.True(manager.CanPostEvents());
        Assert.Equal(0, _connector!.Connects);
    }

    [Fact]
    public void The_daemon_starts_a_session_from_the_token_and_nudges_the_pointer_out_and_back()
    {
        _tokens.Write("saved");
        using var manager = Manager();

        var result = manager.Inject();

        Assert.True(result.Posted);
        Assert.Equal(["NotifyPointerMotion", "NotifyPointerMotion"], _portal.Calls.Where(c => c.Member.StartsWith("Notify", StringComparison.Ordinal)).Select(c => c.Member));
        var moves = _portal.CallsTo("NotifyPointerMotion").Select(c => (((DBusDouble)c.Body[2]).Value, ((DBusDouble)c.Body[3]).Value)).ToList();
        Assert.Equal([(1.0, 0.0), (-1.0, 0.0)], moves);
        Assert.Equal(FakePortalConnection.SessionPath, ((DBusObjectPath)_portal.CallsTo("NotifyPointerMotion").First().Body[0]).Value);
        Assert.Equal("token_after", _tokens.Read()); // single use: the token the portal returned replaces the one that was used
    }

    [Fact]
    public void The_session_is_kept_for_the_next_injection()
    {
        _tokens.Write("saved");
        using var manager = Manager();

        manager.Inject();
        manager.Inject();

        Assert.Equal(1, _connector!.Connects);
        Assert.Single(_portal.CallsTo("CreateSession"));
        Assert.Equal(4, _portal.CallsTo("NotifyPointerMotion").Count());
        Assert.True(manager.CanPostEvents());
    }

    [Fact]
    public void The_F15_strategy_releases_the_key_by_keysym()
    {
        _tokens.Write("saved");
        using var manager = Manager(InjectionStrategy.F15KeyUp);

        var result = manager.Inject();

        Assert.True(result.Posted);
        var call = Assert.Single(_portal.CallsTo("NotifyKeyboardKeysym"));
        Assert.Equal([new DBusInt32(0xffcc), new DBusUInt32(0)], call.Body.Skip(2));
        Assert.Empty(_portal.CallsTo("NotifyPointerMotion"));
    }

    [Theory]
    [InlineData(InjectionStrategy.MouseNudge, 1u, "pointer")]
    [InlineData(InjectionStrategy.F15KeyUp, 2u, "keyboard")]
    public void A_consent_without_the_device_the_strategy_needs_is_a_failure_that_says_so(InjectionStrategy strategy, uint granted, string missing)
    {
        _portal.GrantedDevices = granted;
        _tokens.Write("saved");
        using var manager = Manager(strategy);

        var result = manager.Inject();

        Assert.False(result.Posted);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void A_token_the_portal_does_not_accept_is_removed_so_that_no_further_attempt_can_show_a_dialog(uint response)
    {
        _portal.StartResponse = response;
        _tokens.Write("saved");
        using var manager = Manager();

        var result = manager.Inject();

        Assert.False(result.Posted);
        Assert.Contains("was removed", result.Error, StringComparison.Ordinal);
        Assert.False(_tokens.Exists());
        Assert.False(manager.CanPostEvents()); // Degraded (PermissionMissing) from now on
        Assert.False(manager.Inject().Posted);
        Assert.Single(_portal.CallsTo("CreateSession")); // the second attempt never reached the portal
    }

    [Fact]
    public void A_start_that_is_not_answered_closes_the_request_and_drops_the_token()
    {
        _portal.StartSilence = true;
        _tokens.Write("saved");
        using var manager = Manager();

        var result = manager.Inject();

        Assert.False(result.Posted);
        Assert.False(_tokens.Exists());
        Assert.Contains(_portal.CallsTo("Close"), c => c.Interface == PortalProtocol.RequestInterface);
    }

    [Fact]
    public void A_portal_that_is_not_there_yet_keeps_the_token_for_the_next_try()
    {
        _portal.Version = null;
        _tokens.Write("saved");
        using var manager = Manager();

        var result = manager.Inject();

        Assert.False(result.Posted);
        Assert.Contains("xdg-desktop-portal", result.Error, StringComparison.Ordinal);
        Assert.Equal("saved", _tokens.Read());
        Assert.True(manager.CanPostEvents());
    }

    [Fact]
    public void A_failed_notify_discards_the_session_and_the_next_attempt_opens_a_new_one()
    {
        _tokens.Write("saved");
        using var manager = Manager();
        Assert.True(manager.Inject().Posted);

        _portal.FailNotify = true;
        var failed = manager.Inject();
        _portal.FailNotify = false;
        var again = manager.Inject();

        Assert.False(failed.Posted);
        Assert.Contains("opened again", failed.Error, StringComparison.Ordinal);
        Assert.True(again.Posted);
        Assert.Equal(2, _connector!.Connects);
    }

    [Fact]
    public void A_session_the_portal_closed_is_replaced_at_the_next_injection()
    {
        _tokens.Write("saved");
        using var manager = Manager();
        manager.Inject();

        _portal.Emit(new DBusSignal(FakePortalConnection.SessionPath, PortalProtocol.SessionInterface, "Closed", [DBusDict.Empty]));
        var result = manager.Inject();

        Assert.True(result.Posted);
        Assert.Equal(2, _connector!.Connects);
    }

    [Fact]
    public void A_portal_that_returns_no_new_token_leaves_a_warning_and_no_stale_token()
    {
        _portal.NewRestoreToken = null;
        _tokens.Write("saved");
        using var manager = Manager();

        Assert.True(manager.Inject().Posted);

        Assert.False(_tokens.Exists());
        Assert.Contains(manager.Diagnose(), d => d.Label == "Portal warning:");
        Assert.True(manager.CanPostEvents()); // the open session still works
    }

    [Fact]
    public void Diagnose_reports_the_consent_and_the_session_without_touching_the_bus()
    {
        using var manager = Manager();
        Assert.Contains(manager.Diagnose(), d => d is { Label: "Portal consent:", Value: var v } && v.Contains("none saved", StringComparison.Ordinal));
        Assert.Contains(manager.Diagnose(), d => d is { Label: "Portal session:", Value: "not open" });

        _tokens.Write("saved");
        manager.Inject();

        Assert.Contains(manager.Diagnose(), d => d is { Label: "Portal consent:", Value: "saved" });
        Assert.Contains(manager.Diagnose(), d => d is { Label: "Portal session:", Value: "open (pointer and keyboard)" });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requesting_consent_saves_the_token_and_keeps_the_session_for_injection()
    {
        using var manager = Manager();

        var result = await manager.RequestConsentAsync(Token);

        Assert.True(result.Granted);
        Assert.Equal(ConsentOutcome.Granted, result.Outcome);
        Assert.Equal("token_after", _tokens.Read());
        Assert.False(_portal.Disposed); // the connection is the session; it stays open
        Assert.DoesNotContain(_portal.CallsTo("Close"), c => c.Interface == PortalProtocol.SessionInterface);
        Assert.Contains(manager.Diagnose(), d => d is { Label: "Portal session:", Value: "open (pointer and keyboard)" });

        Assert.True(manager.Inject().Posted); // the same session, no second dialog or start
        Assert.Single(_portal.CallsTo("Start"));
        Assert.Equal(2, _portal.CallsTo("NotifyPointerMotion").Count());
    }

    [Fact]
    public async Task Requesting_consent_again_replaces_the_open_session_and_closes_the_old_one()
    {
        var first = new FakePortalConnection { NewRestoreToken = "first_token" };
        var second = new FakePortalConnection { NewRestoreToken = "second_token" };
        var connections = new Queue<FakePortalConnection>([first, second]);
        using var manager = new PortalSessionManager(new PortalClient(new FakeConnector(_ => connections.Dequeue()), Quick), _tokens, new LinuxOptions());

        Assert.True((await manager.RequestConsentAsync(Token)).Granted);
        Assert.True((await manager.RequestConsentAsync(Token)).Granted);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Equal("second_token", _tokens.Read());
        Assert.True(manager.Inject().Posted);
        Assert.Equal(2, second.CallsTo("NotifyPointerMotion").Count());
        Assert.Empty(first.CallsTo("NotifyPointerMotion"));
    }

    [Fact]
    public async Task Requesting_consent_again_offers_the_saved_token_so_that_a_valid_one_needs_no_dialog()
    {
        _tokens.Write("saved");
        using var manager = Manager();

        await manager.RequestConsentAsync(Token);

        Assert.True(_portal.SelectDevicesOptions[0].TryGetString("restore_token", out var sent));
        Assert.Equal("saved", sent);
    }

    [Fact]
    public async Task A_cancelled_request_is_cancelled_and_a_used_up_token_is_removed()
    {
        _portal.StartResponse = 1;
        _tokens.Write("saved");
        using var manager = Manager();

        var result = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancelled", result.Message, StringComparison.Ordinal);
        Assert.False(_tokens.Exists());
        Assert.True(_portal.Disposed);
    }

    [Fact]
    public async Task A_cancelled_first_request_saves_nothing()
    {
        _portal.StartResponse = 1;
        using var manager = Manager();

        Assert.False((await manager.RequestConsentAsync(Token)).Granted);
        Assert.False(_tokens.Exists());
    }

    [Fact]
    public async Task A_request_the_portal_ended_is_a_failure()
    {
        _portal.StartResponse = 2;
        using var manager = Manager();

        var result = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.Failed, result.Outcome);
        Assert.False(_tokens.Exists());
    }

    [Fact]
    public async Task A_dialog_nobody_answers_times_out_and_the_request_is_closed()
    {
        _portal.StartSilence = true;
        var shortWait = new PortalTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150));
        using var manager = new PortalSessionManager(new PortalClient(new FakeConnector(_ => _portal), shortWait), _tokens, new LinuxOptions());

        var result = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.TimedOut, result.Outcome);
        Assert.Single(_portal.CallsTo("Close"), c => c.Interface == PortalProtocol.RequestInterface);
        Assert.False(_tokens.Exists());
    }

    [Fact]
    public async Task A_consent_without_the_needed_device_is_denied_and_saves_nothing()
    {
        _portal.GrantedDevices = 1; // keyboard only; MouseNudge needs the pointer
        using var manager = Manager();

        var result = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.Denied, result.Outcome);
        Assert.Contains("pointer", result.Message, StringComparison.Ordinal);
        Assert.False(_tokens.Exists());
        Assert.True(_portal.Disposed);
    }

    [Fact]
    public async Task A_portal_without_persistence_is_reported_because_the_daemon_could_not_restart()
    {
        _portal.NewRestoreToken = null;
        using var manager = Manager();

        var result = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.Failed, result.Outcome);
        Assert.Contains("no restore token", result.Message, StringComparison.Ordinal);
        Assert.Contains("Uinput", result.Message, StringComparison.Ordinal);
        Assert.False(_tokens.Exists());
    }

    [Fact]
    public async Task A_second_request_while_one_waits_for_the_user_is_in_progress_and_does_not_touch_the_portal()
    {
        _portal.StartSilence = true; // the dialog nobody has answered yet
        using var manager = Manager();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = manager.RequestConsentAsync(stop.Token);
        await WaitUntilAsync(() => _portal.CallsTo("Start").Any());

        var second = await manager.RequestConsentAsync(Token);

        Assert.Equal(ConsentOutcome.InProgress, second.Outcome);
        Assert.Contains("already in progress", second.Message, StringComparison.Ordinal);
        Assert.Single(_portal.CallsTo("Start"));
        Assert.Equal(1, _connector!.Connects);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The flag is released: the next request is a normal one again.
        _portal.StartSilence = false;
        Assert.True((await manager.RequestConsentAsync(Token)).Granted);
    }

    [Fact]
    public async Task The_polling_does_not_use_the_saved_token_while_a_request_waits_for_the_user()
    {
        _tokens.Write("saved");
        _portal.StartSilence = true;
        using var manager = Manager();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = manager.RequestConsentAsync(stop.Token);
        await WaitUntilAsync(() => _portal.CallsTo("Start").Any());

        var injection = manager.Inject();

        Assert.False(injection.Posted);
        Assert.Contains("waiting for an answer", injection.Error, StringComparison.Ordinal);
        Assert.Single(_portal.SelectDevicesOptions); // only the request's own SelectDevices

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10, Token);
        }
    }

    [Fact]
    public void The_input_backend_never_asks_for_consent_in_the_calling_process()
    {
        using var backend = new PortalInputBackend(Manager());

        Assert.False(backend.RequestAccess()); // only reports: no token, no session
        _tokens.Write("saved");
        Assert.True(backend.RequestAccess());

        Assert.Empty(_portal.Calls); // nothing reached the bus
        Assert.Equal(0, _connector!.Connects);
        Assert.Contains("vigil permissions request", backend.NotGrantedHint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_daemons_own_start_uses_the_saved_token_without_asking()
    {
        _tokens.Write("saved");
        using var backend = new PortalInputBackend(Manager());

        Assert.True(backend.CanPostEvents());
        Assert.True(backend.Inject().Posted);
        Assert.Single(_portal.CallsTo("Start")); // the daemon's own start, with the token
    }

    [Fact]
    public async Task The_input_backend_hands_the_request_to_the_session_manager()
    {
        using var backend = new PortalInputBackend(Manager());

        var result = await ((IConsentRequester)backend).RequestConsentAsync(Token);

        Assert.True(result.Granted);
        Assert.Equal("token_after", _tokens.Read());
    }
}
