namespace Vigil.Tray;

/// <summary>
/// Holds an exclusive lock on <c>vigil-tray.lock</c> in the data directory for as long as one tray icon runs, so that starting
/// the tray twice does not give two icons. The same mechanism as the daemon's single-instance lock (see <c>IpcServer</c>); the
/// file is empty, 0600 on Unix, and the lock goes when the process ends, however it ends.
/// </summary>
internal sealed class TrayInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private TrayInstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Null when another tray already holds the lock.</summary>
    public static TrayInstanceLock? TryAcquire(VigilPaths paths)
    {
        paths.EnsureDataDirectory();

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None, // Exclusive: an advisory flock on Unix, a share mode no other open can get past on Windows.
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            return new TrayInstanceLock(new FileStream(paths.TrayLockPath, options));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
