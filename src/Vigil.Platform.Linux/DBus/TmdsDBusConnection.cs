using Tmds.DBus.Protocol;

namespace Vigil.Platform.Linux.DBus;

/// <summary>
/// <see cref="IDBusConnection"/> on Tmds.DBus.Protocol 0.95.2, the one place that library is used. It builds messages with
/// <c>MessageWriter</c> and reads them with <c>Reader</c>, both of which the library documents as NativeAOT and trimming
/// compatible (https://github.com/tmds/Tmds.DBus/blob/main/docs/protocol.md, "NativeAOT/Trimming"); the generic
/// <c>Reader.ReadVariantValue</c> path is used for variants, not the methods the library marks as needing reflection.
/// Library exceptions are turned into <see cref="DBusFailureException"/>, so nothing above this file knows the library.
/// </summary>
internal sealed class TmdsDBusConnection : IDBusConnection
{
    private readonly DBusConnection _connection;

    private TmdsDBusConnection(DBusConnection connection) => _connection = connection;

    public string? UniqueName => _connection.UniqueName;

    public static async Task<TmdsDBusConnection> ConnectAsync(string address, CancellationToken cancellationToken)
    {
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            return new TmdsDBusConnection(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever the reason (no socket, refused authentication, a malformed address), there is no bus to use.
            connection.Dispose();
            throw new DBusUnavailableException($"Cannot connect to the D-Bus bus: {ex.Message}", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task<DBusReply> CallAsync(DBusCall call, CancellationToken cancellationToken)
    {
        try
        {
            var message = BuildMessage(call);

            var body = await _connection.CallMethodAsync(message, static (Message reply, object? _) => ReadBody(reply), null)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DBusReply(body);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new DBusCallFailedException(ex.ErrorName ?? "unknown", ex.ErrorMessage ?? string.Empty);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            throw new DBusUnavailableException($"The D-Bus connection failed: {ex.Message}", ex);
        }
    }

    /// <summary>Every other library exception (a dropped or closed connection, an unreadable message) means the connection cannot be trusted any more.</summary>
    private static bool IsTransportFailure(Exception ex) =>
        ex is DBusExceptionBase or IOException or ObjectDisposedException or System.Net.Sockets.SocketException;

    private MessageBuffer BuildMessage(DBusCall call)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(call.Destination, call.Path, call.Interface, call.Signature, call.Member);
            foreach (var value in call.Body)
            {
                Write(ref writer, value);
            }

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public async Task<IDisposable> WatchSignalAsync(DBusSignalMatch match, Action<DBusSignal> handler, CancellationToken cancellationToken)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = match.Sender,
            Path = match.Path,
            Interface = match.Interface,
            Member = match.Member,
        };

        try
        {
            return await _connection.AddMatchAsync<DBusSignal>(
                rule,
                static (Message signal, object? _) => new DBusSignal(
                    signal.PathAsString ?? string.Empty,
                    signal.InterfaceAsString ?? string.Empty,
                    signal.MemberAsString ?? string.Empty,
                    ReadBody(signal)),
                (Notification<DBusSignal> notification) =>
                {
                    if (notification.HasValue)
                    {
                        handler(notification.Value);
                    }
                },
                false,
                ObserverFlags.None,
                null).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            throw new DBusUnavailableException($"The D-Bus connection failed: {ex.Message}", ex);
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Reads a whole message body by its signature. The types the model carries are read; at a type it does not carry (a
    /// struct, a general array) reading stops and the rest is reported as <see cref="DBusUnsupported"/>, because the
    /// position of what follows cannot be known without reading it.
    /// </summary>
    private static List<DBusValue> ReadBody(Message message)
    {
        var values = new List<DBusValue>();
        var signature = message.SignatureAsString ?? string.Empty;
        var reader = message.GetBodyReader();
        for (var i = 0; i < signature.Length; i++)
        {
            switch (signature[i])
            {
                case 'b':
                    values.Add(new DBusBool(reader.ReadBool()));
                    break;
                case 'i':
                    values.Add(new DBusInt32(reader.ReadInt32()));
                    break;
                case 'u':
                    values.Add(new DBusUInt32(reader.ReadUInt32()));
                    break;
                case 't':
                    values.Add(new DBusUInt64(reader.ReadUInt64()));
                    break;
                case 'd':
                    values.Add(new DBusDouble(reader.ReadDouble()));
                    break;
                case 's':
                    values.Add(new DBusString(reader.ReadString()));
                    break;
                case 'o':
                    values.Add(new DBusObjectPath(reader.ReadObjectPathAsString()));
                    break;
                case 'v':
                    values.Add(new DBusVariant(Convert(reader.ReadVariantValue())));
                    break;
                case 'a' when string.CompareOrdinal(signature, i, "a{sv}", 0, 5) == 0:
                    values.Add(ReadDictionary(ref reader));
                    i += 4;
                    break;
                default:
                    values.Add(new DBusUnsupported(signature[i..]));
                    return values;
            }
        }

        return values;
    }

    private static DBusDict ReadDictionary(ref Reader reader)
    {
        var entries = new List<KeyValuePair<string, DBusValue>>();
        foreach (var entry in reader.ReadDictionaryOfStringToVariantValue())
        {
            entries.Add(new KeyValuePair<string, DBusValue>(entry.Key, Convert(entry.Value)));
        }

        return new DBusDict(entries);
    }

    private static DBusValue Convert(VariantValue value) => value.Type switch
    {
        VariantValueType.Bool => new DBusBool(value.GetBool()),
        VariantValueType.Int32 => new DBusInt32(value.GetInt32()),
        VariantValueType.UInt32 => new DBusUInt32(value.GetUInt32()),
        VariantValueType.UInt64 => new DBusUInt64(value.GetUInt64()),
        VariantValueType.Double => new DBusDouble(value.GetDouble()),
        VariantValueType.String => new DBusString(value.GetString()),
        VariantValueType.ObjectPath => new DBusObjectPath(value.GetObjectPathAsString()),
        VariantValueType.Variant => Convert(value.GetVariantValue()),
        _ => new DBusUnsupported(value.Type.ToString()),
    };

    private static void Write(ref MessageWriter writer, DBusValue value)
    {
        switch (value)
        {
            case DBusBool v:
                writer.WriteBool(v.Value);
                break;
            case DBusInt32 v:
                writer.WriteInt32(v.Value);
                break;
            case DBusUInt32 v:
                writer.WriteUInt32(v.Value);
                break;
            case DBusUInt64 v:
                writer.WriteUInt64(v.Value);
                break;
            case DBusDouble v:
                writer.WriteDouble(v.Value);
                break;
            case DBusString v:
                writer.WriteString(v.Value);
                break;
            case DBusObjectPath v:
                writer.WriteObjectPath(v.Value);
                break;
            case DBusVariant v:
                writer.WriteVariant(ToVariant(v.Inner));
                break;
            case DBusDict v:
                writer.WriteDictionary([.. v.Entries.Select(e => new KeyValuePair<string, VariantValue>(e.Key, ToVariant(e.Value)))]);
                break;
            default:
                throw new NotSupportedException($"A D-Bus value of type {value.GetType().Name} cannot be sent.");
        }
    }

    private static VariantValue ToVariant(DBusValue value) => value switch
    {
        DBusBool v => VariantValue.Bool(v.Value),
        DBusInt32 v => VariantValue.Int32(v.Value),
        DBusUInt32 v => VariantValue.UInt32(v.Value),
        DBusUInt64 v => VariantValue.UInt64(v.Value),
        DBusDouble v => VariantValue.Double(v.Value),
        DBusString v => VariantValue.String(v.Value),
        DBusObjectPath v => VariantValue.ObjectPath(new ObjectPath(v.Value)),
        _ => throw new NotSupportedException($"A D-Bus value of type {value.GetType().Name} cannot be sent inside a variant."),
    };
}
