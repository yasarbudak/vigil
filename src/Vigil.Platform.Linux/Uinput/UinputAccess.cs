namespace Vigil.Platform.Linux.Uinput;

internal enum UinputOpenResult
{
    Writable,

    /// <summary>There is no such device node: the uinput module is not loaded.</summary>
    Missing,

    /// <summary>The node exists and this user may not write to it: no udev rule or group membership.</summary>
    Denied,

    Failed,
}

/// <summary>
/// Whether /dev/uinput can be written by this user, found by opening it for writing and closing it again: no virtual device
/// exists until UI_DEV_CREATE, so the probe has no effect. Pure apart from the opener, which tests replace.
/// </summary>
internal static class UinputAccess
{
    /// <returns>The first path that can be written, or null with a message for the user.</returns>
    public static string? Probe(Func<string, UinputOpenResult> tryOpen, out string? problem)
    {
        var results = UinputProtocol.DevicePaths.Select(path => (Path: path, Result: tryOpen(path))).ToList();
        if (results.FirstOrDefault(r => r.Result == UinputOpenResult.Writable) is { Path: not null } writable)
        {
            problem = null;
            return writable.Path;
        }

        if (results.Any(r => r.Result == UinputOpenResult.Denied))
        {
            var denied = results.First(r => r.Result == UinputOpenResult.Denied).Path;
            problem = $"{denied} exists, but you may not write to it. Give your user access with a udev rule and a group (see the README, 'Linux: uinput'); Vigil never asks for root.";
            return null;
        }

        problem = results.All(r => r.Result == UinputOpenResult.Missing)
            ? "There is no /dev/uinput: the uinput kernel module is not loaded (modprobe uinput as root, and see the README, 'Linux: uinput')."
            : "/dev/uinput cannot be opened.";
        return null;
    }

    /// <summary>The real opener: open for write, close at once.</summary>
    public static UinputOpenResult TryOpen(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return UinputOpenResult.Writable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return UinputOpenResult.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return UinputOpenResult.Denied;
        }
        catch (IOException)
        {
            return UinputOpenResult.Failed;
        }
    }
}
