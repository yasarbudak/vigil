using System.Text;

namespace Vigil.Tests;

/// <summary>
/// Reads a file the way a reader has to while another process still has it open for writing. <c>File.ReadAllText</c>
/// and <c>File.ReadAllLines</c> open with <see cref="FileShare.Read"/>, which Windows refuses while a writer holds the
/// file; <see cref="FileShare.ReadWrite"/> is accepted on every platform.
/// </summary>
internal static class SharedFile
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    public static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }
}
