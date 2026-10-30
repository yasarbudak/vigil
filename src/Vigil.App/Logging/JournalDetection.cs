using System.Globalization;

namespace Vigil.Logging;

/// <summary>
/// Tells whether this process's standard output or error is connected to the systemd journal, in which case journald is the
/// log (it timestamps, rotates and caps the lines by itself, <c>journalctl --user -u vigil</c> shows them) and the daemon
/// writes to the console only, with no <c>vigil.log</c>. systemd.exec(5), <c>$JOURNAL_STREAM</c>: "If the standard output or
/// standard error output of the executed processes are connected to the journal ... <c>$JOURNAL_STREAM</c> contains the device
/// and inode numbers of the connection file descriptor, formatted in decimal, separated by a colon". The documentation says
/// the variable alone is not enough ("services might invoke external processes replacing their standard output or standard
/// error output, without unsetting the environment variable") and that the numbers "should be compared" with the
/// descriptors, which is what this does. <c>$INVOCATION_ID</c> is not used: it only says "running as a unit", not where the
/// output goes.
/// https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html#$JOURNAL_STREAM
/// <para>
/// On Linux a socket descriptor's link in <c>/proc/self/fd</c> reads <c>socket:[inode]</c>, which has the inode but not the
/// device number. The inode of a socket is unique among sockets (they all belong to one pseudo file system), so the inode
/// alone identifies the journal stream.
/// </para>
/// </summary>
internal static class JournalDetection
{
    /// <param name="getenv">Reads an environment variable.</param>
    /// <param name="readFdLink">Reads the link target of <c>/proc/self/fd/&lt;n&gt;</c>, or null when it cannot.</param>
    public static bool IsAttached(Func<string, string?> getenv, Func<int, string?> readFdLink)
    {
        if (!TryParseStream(getenv("JOURNAL_STREAM"), out var inode))
        {
            return false;
        }

        // Standard error first: "that is usually the preferred destination for log data", and the variable describes it when both are connected.
        foreach (var fd in new[] { 2, 1 })
        {
            if (ParseSocketInode(readFdLink(fd)) == inode)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAttachedToThisProcess() =>
        OperatingSystem.IsLinux()
        && IsAttached(Environment.GetEnvironmentVariable, fd => ReadLink($"/proc/self/fd/{fd}"));

    /// <summary>"device:inode", both decimal.</summary>
    internal static bool TryParseStream(string? value, out ulong inode)
    {
        inode = 0;
        var parts = value?.Split(':');
        return parts is [var device, var node]
            && ulong.TryParse(device, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(node, NumberStyles.None, CultureInfo.InvariantCulture, out inode);
    }

    /// <summary>"socket:[12345]" gives 12345; anything else gives null.</summary>
    internal static ulong? ParseSocketInode(string? link)
    {
        const string prefix = "socket:[";
        if (link is null || !link.StartsWith(prefix, StringComparison.Ordinal) || !link.EndsWith(']'))
        {
            return null;
        }

        return ulong.TryParse(link.AsSpan(prefix.Length, link.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
            ? inode
            : null;
    }

    private static string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
