using System.Globalization;
using System.Text;

namespace Vigil.Platform.Linux.DBus;

/// <summary>
/// A D-Bus value, limited to the types Vigil sends and reads. Vigil's own small model sits between the code that
/// decides what to ask (the portal flow, the idle and lock readers) and the library that puts it on the wire
/// (<see cref="TmdsDBusConnection"/>, the only file that references Tmds.DBus.Protocol), so that the message
/// building and parsing can be tested with a fake connection and no bus.
/// Type codes: https://dbus.freedesktop.org/doc/dbus-specification.html#basic-types
/// </summary>
internal abstract record DBusValue
{
    /// <summary>The D-Bus type signature of this value ("b", "u", "a{sv}", ...).</summary>
    public abstract string Signature { get; }

    public static string SignatureOf(IEnumerable<DBusValue> body)
    {
        var signature = new StringBuilder();
        foreach (var value in body)
        {
            signature.Append(value.Signature);
        }

        return signature.ToString();
    }
}

internal sealed record DBusBool(bool Value) : DBusValue
{
    public override string Signature => "b";
}

internal sealed record DBusInt32(int Value) : DBusValue
{
    public override string Signature => "i";
}

internal sealed record DBusUInt32(uint Value) : DBusValue
{
    public override string Signature => "u";
}

internal sealed record DBusUInt64(ulong Value) : DBusValue
{
    public override string Signature => "t";
}

internal sealed record DBusDouble(double Value) : DBusValue
{
    public override string Signature => "d";
}

internal sealed record DBusString(string Value) : DBusValue
{
    public override string Signature => "s";
}

internal sealed record DBusObjectPath(string Value) : DBusValue
{
    public override string Signature => "o";
}

/// <summary>A value of type <c>v</c>: the inner value travels together with its own signature.</summary>
internal sealed record DBusVariant(DBusValue Inner) : DBusValue
{
    public override string Signature => "v";
}

/// <summary>
/// A dictionary of type <c>a{sv}</c> (a "vardict": the portal option and result maps). The entries hold the values
/// themselves, not <see cref="DBusVariant"/> wrappers: every value of the dictionary is a variant on the wire.
/// </summary>
internal sealed record DBusDict(IReadOnlyList<KeyValuePair<string, DBusValue>> Entries) : DBusValue
{
    public static DBusDict Empty { get; } = new([]);

    public override string Signature => "a{sv}";

    public static DBusDict Of(params (string Key, DBusValue Value)[] entries) =>
        new([.. entries.Select(entry => new KeyValuePair<string, DBusValue>(entry.Key, entry.Value))]);

    public bool TryGetString(string key, out string value)
    {
        value = string.Empty;
        if (Find(key) is DBusString text)
        {
            value = text.Value;
            return true;
        }

        return false;
    }

    /// <summary>A string or an object path: the portal documents <c>session_handle</c> as an object path "erroneously implemented as s".</summary>
    public bool TryGetText(string key, out string value)
    {
        value = string.Empty;
        switch (Find(key))
        {
            case DBusString text:
                value = text.Value;
                return true;
            case DBusObjectPath path:
                value = path.Value;
                return true;
            default:
                return false;
        }
    }

    public bool TryGetUInt32(string key, out uint value)
    {
        value = 0;
        if (Find(key) is DBusUInt32 number)
        {
            value = number.Value;
            return true;
        }

        return false;
    }

    private DBusValue? Find(string key)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public override string ToString() =>
        "{" + string.Join(", ", Entries.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key}={e.Value}"))) + "}";
}

/// <summary>A value of a type the model does not carry (for example <c>a(ua{sv})</c>); it is skipped, never an error.</summary>
internal sealed record DBusUnsupported(string TypeName) : DBusValue
{
    public override string Signature => "?";
}
