using System.Text;

namespace Vigil.Logging;

/// <summary>
/// Appends lines to one file and keeps its size bounded: when a line would push the file over
/// <c>maxBytes</c>, the file is moved to <c>&lt;path&gt;.1</c> (replacing any earlier backup) and a fresh file is
/// started. One backup is kept, so the disk use is at most about twice <c>maxBytes</c>. A file that is already over
/// the cap when it is opened (for example one that launchd grew before rotation existed) is rotated first.
/// </summary>
/// <remarks>
/// Safe to call from several threads. Logging must never take the daemon down, so an I/O failure drops the line and
/// the file is reopened on the next write. New files are created 0600 on Unix (on Windows they take the data
/// directory's access rules, see <c>PrivateDirectory</c>) and opened with <see cref="FileShare.Read"/> so
/// <c>tail -f</c> works; each line is flushed immediately (the volume is tiny).
/// <para>
/// Windows and readers: the file is held open for writing, so a reader has to open it with at least
/// <see cref="FileShare.ReadWrite"/>. Renaming a file needs delete access, which Windows grants only if every other
/// open handle was opened with <see cref="FileShare.Delete"/> (CreateFile, <c>dwShareMode</c>: FILE_SHARE_DELETE
/// "enables subsequent open operations on a file to request delete access"; "Delete access allows both delete and
/// rename operations"; https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea). That is
/// the reader's choice, not the writer's: sharing delete on this handle would only let other programs rename the
/// live log, and would not make a reader's handle share it. So when the rename is refused, <see cref="Rotate"/> falls
/// back to copying the file to the backup and truncating it in place, which needs only the write sharing every reader
/// already has. The reader then sees the file shrink, as <c>tail -f</c> does after a copy-and-truncate rotation.
/// </para>
/// </remarks>
internal sealed class RotatingFileWriter : IDisposable
{
    internal const long DefaultMaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private FileStream? _stream;

    /// <param name="maxBytes">Size at which the file is rotated; a constant in production, a parameter only for tests.</param>
    public RotatingFileWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;

        // Opens (and, if oversized, rotates) right away so an old file is dealt with at daemon start.
        lock (_gate)
        {
            TryOpen();
        }
    }

    public string BackupPath => _path + ".1";

    /// <summary>Appends <paramref name="line"/> and a line feed. Never throws.</summary>
    public void WriteLine(string line)
    {
        var bytes = Utf8.GetBytes(line + "\n");

        lock (_gate)
        {
            try
            {
                var stream = _stream ?? Open();
                if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes)
                {
                    stream = Rotate();
                }

                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Nowhere to report a logging failure; drop the line and start over with a new handle next time.
                Close();
            }
        }
    }

    /// <summary>Closes the file. A later write opens it again, so lines logged during shutdown are not lost.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }

    private void TryOpen()
    {
        try
        {
            Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Close(); // The directory may not exist yet; the first write tries again.
        }
    }

    private FileStream Open()
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(_path, options);
        if (stream.Length <= _maxBytes)
        {
            _stream = stream;
            return stream;
        }

        stream.Dispose();
        return Rotate();
    }

    /// <summary>Moves the current file over the backup and opens a new, empty file.</summary>
    private FileStream Rotate()
    {
        Close();
        try
        {
            File.Move(_path, BackupPath, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // A reader holds the file without sharing delete (see the remarks); Unix renames regardless of readers.
            CopyThenTruncate();
        }

        return Open();
    }

    /// <summary>
    /// The Windows fallback of <see cref="Rotate"/>. This process is the only writer, so nothing is appended between
    /// the copy and the truncation. If the truncation fails the backup is already updated and the next write starts
    /// over, which repeats the copy and is harmless.
    /// </summary>
    private void CopyThenTruncate()
    {
        File.Copy(_path, BackupPath, overwrite: true);
        using var truncated = new FileStream(_path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
    }

    private void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
