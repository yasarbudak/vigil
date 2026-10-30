namespace Vigil.Platform.Windows.Autostart;

/// <summary>Where the per-user Run key is.</summary>
public static class RunKeyLocation
{
    /// <summary>Path of the key below HKEY_CURRENT_USER.</summary>
    public const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";
}

/// <summary>What <see cref="IRunKey.Read"/> found under a value name.</summary>
/// <param name="Text">The text of a plain string (REG_SZ) value; null when the value exists but is of another type.</param>
public readonly record struct RunKeyValue(string? Text);

/// <summary>
/// The values of the current user's <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> key, which
/// make a program start when the user signs in. Behind an interface so tests never touch the registry.
/// https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
/// </summary>
public interface IRunKey
{
    /// <summary>The value with this name, or null when there is none.</summary>
    RunKeyValue? Read(string name);

    /// <summary>Creates or replaces the value as a plain string (REG_SZ).</summary>
    void Write(string name, string commandLine);

    /// <summary>Removes the value; false when there was none.</summary>
    bool Delete(string name);
}
