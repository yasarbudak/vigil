using Vigil.Platform.Linux;
using Vigil.Platform.Linux.DBus;

namespace Vigil.Tests;

public class LinuxDBusTests
{
    [Fact]
    public void A_call_body_has_the_signature_of_its_values()
    {
        var call = DBusCall.Create(
            "org.example",
            "/o",
            "org.example.I",
            "M",
            new DBusObjectPath("/s"),
            DBusDict.Empty,
            new DBusDouble(1),
            new DBusInt32(2),
            new DBusUInt32(3),
            new DBusString("x"),
            new DBusBool(true),
            new DBusUInt64(4),
            new DBusVariant(new DBusUInt32(5)));

        Assert.Equal("oa{sv}diusbtv", call.Signature);
        Assert.Equal(string.Empty, DBusCall.Create("d", "/", "i", "m").Signature);
    }

    [Fact]
    public void A_vardict_finds_typed_entries_and_ignores_other_types()
    {
        var dict = DBusDict.Of(("name", new DBusString("v")), ("count", new DBusUInt32(7)), ("flag", new DBusBool(true)));

        Assert.True(dict.TryGetString("name", out var name));
        Assert.Equal("v", name);
        Assert.True(dict.TryGetUInt32("count", out var count));
        Assert.Equal(7u, count);
        Assert.False(dict.TryGetString("count", out _)); // the wrong type is not a string
        Assert.False(dict.TryGetUInt32("missing", out _));
    }

    [Fact]
    public void Text_entries_accept_a_string_or_an_object_path()
    {
        var dict = DBusDict.Of(("a", new DBusString("s")), ("b", new DBusObjectPath("/o")), ("c", new DBusUInt32(1)));

        Assert.True(dict.TryGetText("a", out var a));
        Assert.Equal("s", a);
        Assert.True(dict.TryGetText("b", out var b));
        Assert.Equal("/o", b);
        Assert.False(dict.TryGetText("c", out _));
    }

    private static Func<string, string?> Env(params (string, string)[] variables)
    {
        var map = variables.ToDictionary(v => v.Item1, v => v.Item2);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void The_session_bus_address_comes_from_the_variable_first()
    {
        var address = DBusAddresses.Resolve(DBusBus.Session, Env(("DBUS_SESSION_BUS_ADDRESS", "unix:path=/x/bus"), ("XDG_RUNTIME_DIR", "/run/user/1000")), _ => true);

        Assert.Equal("unix:path=/x/bus", address);
    }

    [Theory]
    [InlineData("tcp:host=example.org,port=1234", null)]
    [InlineData("tcp:host=a,port=1;unix:path=/run/user/1000/bus", "unix:path=/run/user/1000/bus")]
    [InlineData(" unix:path=/a ; unix:path=/b", "unix:path=/a")]
    [InlineData("unixexec:path=/bin/ssh", null)]
    public void Only_unix_addresses_are_used_because_Vigil_opens_no_network_connection(string configured, string? expected)
    {
        Assert.Equal(expected, DBusAddresses.Resolve(DBusBus.Session, Env(("DBUS_SESSION_BUS_ADDRESS", configured)), _ => true));
        Assert.Equal(expected, DBusAddresses.Resolve(DBusBus.System, Env(("DBUS_SYSTEM_BUS_ADDRESS", configured)), _ => true));
    }

    [UnixOnlyFact]
    public void Without_the_variable_the_session_bus_is_the_socket_in_the_runtime_directory_if_it_exists()
    {
        var env = Env(("XDG_RUNTIME_DIR", "/run/user/1000"));

        Assert.Equal("unix:path=/run/user/1000/bus", DBusAddresses.Resolve(DBusBus.Session, env, path => path == "/run/user/1000/bus"));
        Assert.Null(DBusAddresses.Resolve(DBusBus.Session, env, _ => false));
    }

    [Fact]
    public void Without_a_runtime_directory_there_is_no_session_bus()
    {
        Assert.Null(DBusAddresses.Resolve(DBusBus.Session, Env(), _ => true));
        Assert.Null(DBusAddresses.Resolve(DBusBus.Session, Env(("XDG_RUNTIME_DIR", "relative/dir")), _ => true));
    }

    [Fact]
    public void The_system_bus_is_the_variable_or_the_well_known_socket()
    {
        Assert.Equal("unix:path=/y", DBusAddresses.Resolve(DBusBus.System, Env(("DBUS_SYSTEM_BUS_ADDRESS", "unix:path=/y")), _ => false));
        Assert.Equal("unix:path=/var/run/dbus/system_bus_socket", DBusAddresses.Resolve(DBusBus.System, Env(), _ => false));
    }

    [Theory]
    [InlineData("/run/user/1000/bus", "/run/user/1000/bus")]
    [InlineData("/run/user/a b/bus", "/run/user/a%20b/bus")]
    [InlineData("/x;y,z=1", "/x%3by%2cz%3d1")]
    [InlineData("/ü", "/%c3%bc")]
    [InlineData("/a\\b", "/a%5cb")]
    public void Address_values_escape_what_the_specification_says(string value, string expected)
    {
        Assert.Equal(expected, DBusAddresses.Escape(value));
    }

    [Fact]
    public void The_client_connects_once_and_reuses_the_connection()
    {
        var connection = new FakeDBusConnection();
        var connector = new FakeConnector(_ => connection);
        using var client = new DBusClient(connector, DBusBus.Session);
        var call = DBusCall.Create("d", "/", "i", "m");

        client.Call(call);
        client.Call(call);

        Assert.Equal(1, connector.Connects);
        Assert.Equal(2, connection.Calls.Count);
    }

    [Fact]
    public void An_error_reply_keeps_the_connection_and_a_transport_failure_drops_it()
    {
        var connections = new List<FakeDBusConnection>();
        var connector = new FakeConnector(_ =>
        {
            var connection = new FakeDBusConnection();
            connections.Add(connection);
            return connection;
        });
        using var client = new DBusClient(connector, DBusBus.System);
        var call = DBusCall.Create("d", "/", "i", "m");

        client.Call(call);
        connections[0].Responder = _ => throw new DBusCallFailedException("org.x.Error", "no");
        Assert.Throws<DBusCallFailedException>(() => client.Call(call));
        Assert.Equal(1, connector.Connects);
        Assert.False(connections[0].Disposed);

        connections[0].Responder = _ => throw new DBusUnavailableException("gone");
        Assert.Throws<DBusUnavailableException>(() => client.Call(call));
        Assert.True(connections[0].Disposed);

        client.Call(call); // connects again
        Assert.Equal(2, connector.Connects);
    }

    [Fact]
    public void A_bus_that_cannot_be_reached_is_reported_as_unavailable()
    {
        var connector = new FakeConnector(_ => throw new DBusUnavailableException("no bus"));
        using var client = new DBusClient(connector, DBusBus.Session);

        Assert.Throws<DBusUnavailableException>(() => client.Call(DBusCall.Create("d", "/", "i", "m")));
    }
}

public class LinuxIdleAndLockTests
{
    private static DBusClient Client(Func<DBusCall, DBusReply> responder, out FakeDBusConnection connection)
    {
        var fake = new FakeDBusConnection { Responder = responder };
        connection = fake;
        return new DBusClient(new FakeConnector(_ => fake), DBusBus.Session);
    }

    [Fact]
    public void The_GNOME_idle_call_has_the_documented_destination_path_interface_and_member()
    {
        var call = GnomeIdleSource.GetIdletimeCall;

        Assert.Equal("org.gnome.Mutter.IdleMonitor", call.Destination);
        Assert.Equal("/org/gnome/Mutter/IdleMonitor/Core", call.Path);
        Assert.Equal("org.gnome.Mutter.IdleMonitor", call.Interface);
        Assert.Equal("GetIdletime", call.Member);
        Assert.Empty(call.Body);
    }

    [Fact]
    public void The_GNOME_idle_time_is_a_uint64_of_milliseconds()
    {
        using var client = Client(_ => new DBusReply([new DBusUInt64(90_500)]), out var connection);

        var idle = new GnomeIdleSource(client).GetIdleTime();

        Assert.Equal(TimeSpan.FromMilliseconds(90_500), idle);
        Assert.Single(connection.Calls);
    }

    [Fact]
    public void An_absurdly_large_idle_time_does_not_overflow()
    {
        Assert.True(GnomeIdleSource.Parse(new DBusReply([new DBusUInt64(ulong.MaxValue)])) > TimeSpan.FromDays(365));
    }

    [Theory]
    [InlineData("s")]
    [InlineData("")]
    public void An_unexpected_reply_is_unavailable_not_a_wrong_number(string signature)
    {
        DBusValue[] body = signature == "s" ? [new DBusString("x")] : [];

        Assert.Throws<LinuxUnavailableException>(() => GnomeIdleSource.Parse(new DBusReply(body)));
    }

    [Fact]
    public void A_compositor_without_the_monitor_gets_a_message_that_names_the_supported_ones()
    {
        using var client = Client(_ => throw new DBusCallFailedException("org.freedesktop.DBus.Error.ServiceUnknown", "x"), out _);

        var ex = Assert.Throws<LinuxUnavailableException>(() => new GnomeIdleSource(client).GetIdleTime());

        Assert.Contains("GNOME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("KDE Plasma refuses GetSessionIdleTime on Wayland", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_bus_is_unavailable_with_the_reason()
    {
        using var client = new DBusClient(new FakeConnector(_ => throw new DBusUnavailableException("no bus")), DBusBus.Session);

        var ex = Assert.Throws<LinuxUnavailableException>(() => new GnomeIdleSource(client).GetIdleTime());

        Assert.Contains("no bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lock_hint_call_reads_LockedHint_of_the_auto_session_on_logind()
    {
        var call = LogindSessionLock.GetLockedHintCall;

        Assert.Equal("org.freedesktop.login1", call.Destination);
        Assert.Equal("/org/freedesktop/login1/session/auto", call.Path);
        Assert.Equal("org.freedesktop.DBus.Properties", call.Interface);
        Assert.Equal("Get", call.Member);
        Assert.Equal([new DBusString("org.freedesktop.login1.Session"), new DBusString("LockedHint")], call.Body);
        Assert.Equal("ss", call.Signature);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_lock_hint_is_the_boolean_inside_the_variant(bool hint, bool expected)
    {
        using var client = Client(_ => new DBusReply([new DBusVariant(new DBusBool(hint))]), out _);

        Assert.Equal(expected, new LogindSessionLock(client).IsLocked());
    }

    [Fact]
    public void Anything_but_a_boolean_or_any_failure_is_unknown()
    {
        Assert.Null(LogindSessionLock.Parse(new DBusReply([new DBusVariant(new DBusString("yes"))])));
        Assert.Null(LogindSessionLock.Parse(new DBusReply([])));

        using var failing = Client(_ => throw new DBusCallFailedException("org.freedesktop.DBus.Error.AccessDenied", "no"), out _);
        Assert.Null(new LogindSessionLock(failing).IsLocked());

        using var unreachable = new DBusClient(new FakeConnector(_ => throw new DBusUnavailableException("no bus")), DBusBus.System);
        Assert.Null(new LogindSessionLock(unreachable).IsLocked());
    }
}
