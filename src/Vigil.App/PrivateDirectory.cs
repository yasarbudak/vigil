namespace Vigil;

/// <summary>
/// Creates directories that only the current user can open: mode 0700 on Unix. On Windows there are no mode bits and
/// Vigil sets no access rules of its own: the directory keeps the ones it inherits from its parent. The default
/// directory, <c>%LOCALAPPDATA%\Vigil</c>, is inside the user's profile, whose default access rules do not give other
/// standard users access (the owner's premise for this design; it has not been checked on a real machine yet). A
/// directory chosen with <c>VIGIL_DATA_DIR</c> is only as private as its parent: on Windows keep it inside your profile.
/// The same holds for every file and for the IPC socket in it.
/// </summary>
internal static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates the directory if needed and sets it to 0700 on Unix (inherited access rules on Windows). Fails if the mode cannot be set (e.g. not the owner).</summary>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Deliberately no mode and no ACL change: see the type's remarks.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);
        File.SetUnixFileMode(path, OwnerOnly);
    }
}
