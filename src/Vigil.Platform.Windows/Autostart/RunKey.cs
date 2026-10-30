using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vigil.Platform.Windows.Autostart;

/// <summary>
/// The real <see cref="IRunKey"/>: the per-user Run key. It needs no administrator rights (HKEY_CURRENT_USER is the
/// user's own hive). Environment variables in an existing value are not expanded when it is read, so what is compared
/// is exactly what is stored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunKey : IRunKey
{
    public RunKeyValue? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyLocation.Path, writable: false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return null;
        }

        return new RunKeyValue(value is string text && key.GetValueKind(name) == RegistryValueKind.String ? text : null);
    }

    public void Write(string name, string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyLocation.Path, writable: true);
        key.SetValue(name, commandLine, RegistryValueKind.String);
    }

    public bool Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyLocation.Path, writable: true);
        if (key?.GetValue(name) is null)
        {
            return false;
        }

        key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }
}
