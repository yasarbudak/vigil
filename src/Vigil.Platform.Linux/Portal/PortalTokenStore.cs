namespace Vigil.Platform.Linux.Portal;

/// <summary>
/// The portal's restore token, in a file of the data directory (0600, written atomically). The token is the handle to a
/// consent the user gave once: with it the daemon can start a RemoteDesktop session after a restart without a dialog
/// ("restore_token ... the token to restore a previous session"). It is a random string, not a credential in the usual
/// sense, but anyone who can read it could ask the portal for the same session in the same application context, so it is
/// private to the user like every other file Vigil writes. The portal tokens are single use ("invalidated after using it
/// once ... use the new restore token sent in response to starting this session"), so every successful start writes the
/// token it was given back.
/// </summary>
internal sealed class PortalTokenStore(string path)
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const int MaxLength = 128;

    public string Path => path;

    /// <summary>
    /// What xdg-desktop-portal accepts as a restore token: the form it generates (a valid object path element) or a UUID
    /// (<c>xdp_session_persistence_validate_restore_token</c> in desktop-portal/xdp-session-persistence.c). Letters, digits,
    /// underscore and hyphen, at most 128 characters, so that nothing read from the file can carry anything else onto the bus.
    /// </summary>
    public static bool IsValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxLength && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>True when a file is there; whether its content is valid is <see cref="Read"/>'s business.</summary>
    public bool Exists() => File.Exists(path);

    /// <returns>The token, or null when there is no file, it cannot be read or its content is not a token.</returns>
    public string? Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return IsValid(text) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the token through a temporary file next to the target (0600) and a rename, so a crash never leaves half a token.</summary>
    /// <exception cref="ArgumentException">The token has characters a restore token cannot have.</exception>
    public void Write(string token)
    {
        if (!IsValid(token))
        {
            throw new ArgumentException("Not a valid restore token.", nameof(token));
        }

        var directory = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory); // The token is only ever written on Linux; this keeps the code portable.
            }
            else
            {
                Directory.CreateDirectory(directory, DirectoryMode);
            }
        }

        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(temporary, options))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(token + "\n");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, OwnerOnly); // Independent of the umask.
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // No-op after a successful move.
        }
    }

    /// <summary>Removes the token file; false when there was none.</summary>
    public bool Delete()
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
