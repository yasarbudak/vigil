namespace Vigil;

/// <summary>Creates directories that only the current user can open.</summary>
internal static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates the directory if needed and sets it to 0700 on Unix. Fails if the mode cannot be set (e.g. not the owner).</summary>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The user profile ACL already restricts %LOCALAPPDATA% to the user.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);
        File.SetUnixFileMode(path, OwnerOnly);
    }
}
