using Vigil.Platform.Linux.DBus;
using Vigil.Platform.Linux.Portal;

namespace Vigil.Tests;

/// <summary>A scripted D-Bus connection: records every call, answers from a function and delivers signals the test emits. No bus is touched.</summary>
internal class FakeDBusConnection : IDBusConnection
{
    private readonly List<(DBusSignalMatch Match, Action<DBusSignal> Handler)> _watches = [];

    public string? UniqueName { get; set; } = ":1.42";

    public List<DBusCall> Calls { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Answers a call; throw <see cref="DBusCallFailedException"/> or <see cref="DBusUnavailableException"/> to fail it.</summary>
    public Func<DBusCall, DBusReply> Responder { get; set; } = _ => new DBusReply([]);

    public virtual Task<DBusReply> CallAsync(DBusCall call, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        try
        {
            return Task.FromResult(Responder(call));
        }
        catch (DBusFailureException ex)
        {
            return Task.FromException<DBusReply>(ex);
        }
    }

    public Task<IDisposable> WatchSignalAsync(DBusSignalMatch match, Action<DBusSignal> handler, CancellationToken cancellationToken)
    {
        var entry = (match, handler);
        lock (_watches)
        {
            _watches.Add(entry);
        }

        return Task.FromResult<IDisposable>(new Subscription(() =>
        {
            lock (_watches)
            {
                _watches.Remove(entry);
            }
        }));
    }

    public int WatchCount
    {
        get
        {
            lock (_watches)
            {
                return _watches.Count;
            }
        }
    }

    public void Emit(DBusSignal signal)
    {
        List<Action<DBusSignal>> targets;
        lock (_watches)
        {
            targets = [.. _watches
                .Where(w => w.Match.Path == signal.Path && w.Match.Interface == signal.Interface && w.Match.Member == signal.Member)
                .Select(w => w.Handler)];
        }

        foreach (var target in targets)
        {
            target(signal);
        }
    }

    public void Dispose() => Disposed = true;

    public IEnumerable<DBusCall> CallsTo(string member)
    {
        lock (Calls)
        {
            return [.. Calls.Where(c => c.Member == member)];
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

internal sealed class FakeConnector(Func<DBusBus, IDBusConnection> connect) : IDBusConnector
{
    public int Connects { get; private set; }

    public Task<IDBusConnection> ConnectAsync(DBusBus bus, CancellationToken cancellationToken)
    {
        Connects++;
        try
        {
            return Task.FromResult(connect(bus));
        }
        catch (DBusFailureException ex)
        {
            return Task.FromException<IDBusConnection>(ex);
        }
    }
}

/// <summary>
/// Plays the part of xdg-desktop-portal's RemoteDesktop interface at the level the real one is used: property reads, three
/// requests that answer with a Response signal on the predicted request path, and the Notify methods.
/// </summary>
internal sealed class FakePortalConnection : FakeDBusConnection
{
    public uint? Version { get; set; } = 2;

    public uint AvailableDeviceTypes { get; set; } = 3;

    public const string SessionPath = "/org/freedesktop/portal/desktop/session/1_42/vigil_session";

    /// <summary>Response code of Start: 0 success, 1 the user cancelled, 2 ended.</summary>
    public uint StartResponse { get; set; }

    /// <summary>When true Start never answers, as a consent dialog nobody clicks.</summary>
    public bool StartSilence { get; set; }

    public uint GrantedDevices { get; set; } = 3;

    public string? NewRestoreToken { get; set; } = "token_after";

    public bool FailNotify { get; set; }

    public List<DBusDict> SelectDevicesOptions { get; } = [];

    public FakePortalConnection() => Responder = Respond;

    private DBusReply Respond(DBusCall call)
    {
        switch (call.Member)
        {
            case "Get":
                var name = ((DBusString)call.Body[1]).Value;
                if (Version is null)
                {
                    throw new DBusCallFailedException("org.freedesktop.DBus.Error.ServiceUnknown", "The name is not owned");
                }

                return new DBusReply([new DBusVariant(new DBusUInt32(name == "version" ? Version.Value : AvailableDeviceTypes))]);

            case "CreateSession":
                return Request(call, (DBusDict)call.Body[0], DBusDict.Of(("session_handle", new DBusString(SessionPath))), 0);

            case "SelectDevices":
                SelectDevicesOptions.Add((DBusDict)call.Body[1]);
                return Request(call, (DBusDict)call.Body[1], DBusDict.Empty, 0);

            case "Start":
                if (StartSilence)
                {
                    return new DBusReply([new DBusObjectPath(RequestPathOf((DBusDict)call.Body[2]))]);
                }

                var entries = new List<KeyValuePair<string, DBusValue>> { new("devices", new DBusUInt32(GrantedDevices)) };
                if (NewRestoreToken is not null)
                {
                    entries.Add(new("restore_token", new DBusString(NewRestoreToken)));
                }

                var results = StartResponse == 0 ? new DBusDict(entries) : DBusDict.Empty;
                return Request(call, (DBusDict)call.Body[2], results, StartResponse);

            case "NotifyPointerMotion" or "NotifyKeyboardKeysym":
                return FailNotify
                    ? throw new DBusCallFailedException("org.freedesktop.DBus.Error.UnknownObject", "No such session")
                    : new DBusReply([]);

            default:
                return new DBusReply([]);
        }
    }

    private string RequestPathOf(DBusDict options)
    {
        options.TryGetString("handle_token", out var token);
        return PortalProtocol.RequestPath(UniqueName!, token);
    }

    private DBusReply Request(DBusCall call, DBusDict options, DBusDict results, uint code)
    {
        var path = RequestPathOf(options);
        Emit(new DBusSignal(path, PortalProtocol.RequestInterface, "Response", [new DBusUInt32(code), results]));
        return new DBusReply([new DBusObjectPath(path)]);
    }
}
