using Vigil.Platform.Linux;
using Vigil.Platform.Linux.DBus;
using Vigil.Platform.Linux.Portal;

namespace Vigil.Tests;

public class LinuxPortalProtocolTests
{
    [Fact]
    public void The_request_path_is_built_from_the_unique_name_as_the_interface_description_says()
    {
        // "SENDER is the caller's unique name, with the initial ':' removed and all '.' replaced by '_'"
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/tok", PortalProtocol.RequestPath(":1.42", "tok"));
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_2_3/vigil_ab", PortalProtocol.RequestPath(":1.2.3", "vigil_ab"));
    }

    [Fact]
    public void Handle_tokens_are_valid_object_path_elements_and_not_repeated()
    {
        var first = PortalProtocol.NewHandleToken();
        var second = PortalProtocol.NewHandleToken();

        Assert.True(PortalProtocol.IsValidHandleToken(first));
        Assert.StartsWith("vigil_", first, StringComparison.Ordinal);
        Assert.Equal("vigil_".Length + 32, first.Length); // 128 random bits as hex
        Assert.NotEqual(first, second);
        Assert.False(PortalProtocol.IsValidHandleToken("a/b"));
        Assert.False(PortalProtocol.IsValidHandleToken("a-b"));
        Assert.False(PortalProtocol.IsValidHandleToken(string.Empty));
    }

    [Fact]
    public void CreateSession_carries_both_tokens_in_a_vardict()
    {
        var call = PortalProtocol.CreateSession("h", "s");

        Assert.Equal(("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.RemoteDesktop", "CreateSession"), (call.Destination, call.Path, call.Interface, call.Member));
        Assert.Equal("a{sv}", call.Signature);
        var options = (DBusDict)call.Body[0];
        Assert.True(options.TryGetString("handle_token", out var handle));
        Assert.Equal("h", handle);
        Assert.True(options.TryGetString("session_handle_token", out var session));
        Assert.Equal("s", session);
    }

    [Fact]
    public void SelectDevices_asks_for_keyboard_and_pointer_with_persistence_until_revoked()
    {
        var call = PortalProtocol.SelectDevices("/session", "h", PortalProtocol.KeyboardDevice | PortalProtocol.PointerDevice, restoreToken: null);

        Assert.Equal("SelectDevices", call.Member);
        Assert.Equal("oa{sv}", call.Signature);
        Assert.Equal(new DBusObjectPath("/session"), call.Body[0]);
        var options = (DBusDict)call.Body[1];
        Assert.True(options.TryGetUInt32("types", out var types));
        Assert.Equal(3u, types); // KEYBOARD 1 | POINTER 2
        Assert.True(options.TryGetUInt32("persist_mode", out var persist));
        Assert.Equal(2u, persist); // "Permissions persist until explicitly revoked"
        Assert.False(options.TryGetString("restore_token", out _));
    }

    [Fact]
    public void SelectDevices_passes_the_restore_token_when_there_is_one()
    {
        var options = (DBusDict)PortalProtocol.SelectDevices("/s", "h", 3, "saved_token").Body[1];

        Assert.True(options.TryGetString("restore_token", out var token));
        Assert.Equal("saved_token", token);
    }

    [Fact]
    public void Start_has_the_empty_parent_window_the_documentation_allows()
    {
        var call = PortalProtocol.Start("/session", "h");

        Assert.Equal("Start", call.Member);
        Assert.Equal("osa{sv}", call.Signature);
        Assert.Equal(new DBusString(string.Empty), call.Body[1]);
    }

    [Fact]
    public void NotifyPointerMotion_is_session_options_dx_dy()
    {
        var call = PortalProtocol.NotifyPointerMotion("/session", 1, -1);

        Assert.Equal("NotifyPointerMotion", call.Member);
        Assert.Equal("oa{sv}dd", call.Signature);
        Assert.Equal([new DBusObjectPath("/session"), DBusDict.Empty, new DBusDouble(1), new DBusDouble(-1)], call.Body);
    }

    [Fact]
    public void NotifyKeyboardKeysym_releases_F15()
    {
        var call = PortalProtocol.NotifyKeyboardKeysym("/session", Keysyms.F15, PortalProtocol.KeyReleased);

        Assert.Equal("NotifyKeyboardKeysym", call.Member);
        Assert.Equal("oa{sv}iu", call.Signature);
        Assert.Equal(new DBusInt32(0xffcc), call.Body[2]);
        Assert.Equal(new DBusUInt32(0), call.Body[3]); // state 0: Released
    }

    [Fact]
    public void A_Response_signal_is_a_code_and_a_vardict_and_Start_results_are_read_from_it()
    {
        var signal = new DBusSignal(
            "/p",
            "org.freedesktop.portal.Request",
            "Response",
            [new DBusUInt32(0), DBusDict.Of(("devices", new DBusUInt32(2)), ("restore_token", new DBusString("tok-1_x")))]);

        var response = PortalResponse.From(signal)!;

        Assert.Equal(0u, response.Code);
        Assert.Equal((2u, "tok-1_x"), response.ReadStartResults());
        Assert.Null(PortalResponse.From(signal with { Body = [new DBusString("x")] }));
    }

    [Fact]
    public void A_restore_token_with_unexpected_characters_is_not_taken()
    {
        var response = new PortalResponse(0, DBusDict.Of(("devices", new DBusUInt32(3)), ("restore_token", new DBusString("bad token/../"))));

        Assert.Equal((3u, (string?)null), response.ReadStartResults());
    }
}

public class LinuxPortalClientTests
{
    private static readonly PortalTimeouts Quick = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));

    private static (PortalClient Client, FakePortalConnection Portal, FakeConnector Connector) Setup(Action<FakePortalConnection>? configure = null)
    {
        var portal = new FakePortalConnection();
        configure?.Invoke(portal);
        var connector = new FakeConnector(_ => portal);
        return (new PortalClient(connector, Quick), portal, connector);
    }

    [Fact]
    public async Task A_first_consent_runs_the_documented_sequence_and_returns_the_new_restore_token()
    {
        var (client, portal, _) = Setup();

        var result = await client.EstablishAsync(null, PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Established, result.Outcome);
        Assert.Equal("token_after", result.RestoreToken);
        Assert.False(result.TokenSent);
        Assert.Equal(3u, result.Session!.Devices);
        Assert.Equal(FakePortalConnection.SessionPath, result.Session.Path);
        Assert.Equal(["Get", "Get", "CreateSession", "SelectDevices", "Start"], portal.Calls.Select(c => c.Member));
        Assert.False(portal.Disposed); // the session owns the connection
        Assert.False(result.Session.IsClosed);
        Assert.False(portal.SelectDevicesOptions[0].TryGetString("restore_token", out _));
    }

    [Fact]
    public async Task The_response_signals_are_subscribed_before_the_calls_and_released_afterwards()
    {
        var (client, portal, _) = Setup();

        var result = await client.EstablishAsync(null, PortalInteraction.Silent, CancellationToken.None);

        Assert.Equal(PortalOutcome.Established, result.Outcome);
        Assert.Equal(1, portal.WatchCount); // only the Session.Closed watch is left
    }

    [Fact]
    public async Task A_saved_token_is_sent_with_SelectDevices_and_marked_as_used()
    {
        var (client, portal, _) = Setup();

        var result = await client.EstablishAsync("saved_token", PortalInteraction.Silent, CancellationToken.None);

        Assert.True(result.TokenSent);
        Assert.True(portal.SelectDevicesOptions[0].TryGetString("restore_token", out var sent));
        Assert.Equal("saved_token", sent);
    }

    [Fact]
    public async Task A_cancelled_dialog_closes_the_session_and_the_connection()
    {
        var (client, portal, _) = Setup(p => p.StartResponse = 1);

        var result = await client.EstablishAsync("saved_token", PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Cancelled, result.Outcome);
        Assert.True(result.TokenSent);
        Assert.Contains("cancelled", result.Message, StringComparison.Ordinal);
        Assert.Contains(portal.CallsTo("Close"), c => c.Interface == PortalProtocol.SessionInterface);
        Assert.True(portal.Disposed);
    }

    [Fact]
    public async Task A_response_of_two_means_the_portal_ended_the_request()
    {
        var (client, _, _) = Setup(p => p.StartResponse = 2);

        var result = await client.EstablishAsync(null, PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Ended, result.Outcome);
        Assert.Contains("response 2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_answer_a_silent_start_closes_the_request_so_no_dialog_stays()
    {
        var (client, portal, _) = Setup(p => p.StartSilence = true);

        var result = await client.EstablishAsync("saved_token", PortalInteraction.Silent, CancellationToken.None);

        Assert.Equal(PortalOutcome.TimedOut, result.Outcome);
        var close = Assert.Single(portal.CallsTo("Close"), c => c.Interface == PortalProtocol.RequestInterface);
        Assert.StartsWith("/org/freedesktop/portal/desktop/request/1_42/vigil_", close.Path, StringComparison.Ordinal);
        Assert.True(portal.Disposed);
    }

    [Fact]
    public async Task A_missing_portal_is_unavailable_and_says_what_to_install()
    {
        var (client, portal, _) = Setup(p => p.Version = null);

        var result = await client.EstablishAsync("saved_token", PortalInteraction.Silent, CancellationToken.None);

        Assert.Equal(PortalOutcome.Unavailable, result.Outcome);
        Assert.False(result.TokenSent);
        Assert.Contains("xdg-desktop-portal", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(portal.Calls, c => c.Member == "CreateSession");
        Assert.True(portal.Disposed);
    }

    [Fact]
    public async Task A_version_one_portal_cannot_persist_and_is_refused_with_the_version()
    {
        var (client, _, _) = Setup(p => p.Version = 1);

        var result = await client.EstablishAsync(null, PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Unavailable, result.Outcome);
        Assert.Contains("version 1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backend_without_keyboard_and_pointer_is_unavailable()
    {
        var (client, _, _) = Setup(p => p.AvailableDeviceTypes = 4); // touchscreen only

        var result = await client.EstablishAsync(null, PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Unavailable, result.Outcome);
        Assert.Contains("neither keyboard nor pointer", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bus_that_cannot_be_reached_is_unavailable()
    {
        var client = new PortalClient(new FakeConnector(_ => throw new DBusUnavailableException("no session bus")), Quick);

        var result = await client.EstablishAsync(null, PortalInteraction.Silent, CancellationToken.None);

        Assert.Equal(PortalOutcome.Unavailable, result.Outcome);
        Assert.Contains("no session bus", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_handle_that_differs_from_the_predicted_one_is_subscribed_to_as_well()
    {
        // An old portal returns a handle that is not the predictable path: the client must listen on the one it got.
        var portal = new FakePortalConnection();
        var plain = portal.Responder;
        portal.Responder = call =>
        {
            if (call.Member != "CreateSession")
            {
                return plain(call);
            }

            const string actual = "/org/freedesktop/portal/desktop/request/other/handle";
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                portal.Emit(new DBusSignal(actual, PortalProtocol.RequestInterface, "Response", [new DBusUInt32(0), DBusDict.Of(("session_handle", new DBusString(FakePortalConnection.SessionPath)))]));
            });
            return new DBusReply([new DBusObjectPath(actual)]);
        };
        var client = new PortalClient(new FakeConnector(_ => portal), Quick);

        var result = await client.EstablishAsync(null, PortalInteraction.Interactive, CancellationToken.None);

        Assert.Equal(PortalOutcome.Established, result.Outcome);
    }

    [Fact]
    public async Task The_session_notices_the_Closed_signal_and_closes_itself_on_dispose()
    {
        var (client, portal, _) = Setup();
        var result = await client.EstablishAsync(null, PortalInteraction.Silent, CancellationToken.None);
        var session = result.Session!;

        portal.Emit(new DBusSignal(session.Path, PortalProtocol.SessionInterface, "Closed", [DBusDict.Empty]));
        Assert.True(session.IsClosed);

        session.Dispose();
        Assert.True(portal.Disposed);
        Assert.Contains(portal.CallsTo("Close"), c => c.Interface == PortalProtocol.SessionInterface && c.Path == session.Path);
    }
}
