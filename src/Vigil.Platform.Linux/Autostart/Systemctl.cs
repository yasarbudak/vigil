using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Vigil.Platform.Linux.Autostart;

/// <summary>
/// Runs <c>systemctl --user ...</c> (found through PATH, as systemd's own tools expect), with the arguments as a list and
/// never through a shell. systemctl(1): https://www.freedesktop.org/software/systemd/man/latest/systemctl.html
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class Systemctl : ISystemctl
{
    private const string Executable = "systemctl";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public SystemctlResult CheckUserManager()
    {
        var result = Run("--user", "show-environment");
        return new SystemctlResult(result.ExitCode, result.Error); // The environment on stdout is dropped.
    }

    public SystemctlResult DaemonReload() => Combined(Run("--user", "daemon-reload"));

    public SystemctlResult EnableNow(string unit) => Combined(Run("--user", "enable", "--now", unit));

    public SystemctlResult DisableNow(string unit) => Combined(Run("--user", "disable", "--now", unit));

    public bool IsEnabled(string unit) => Run("--user", "--quiet", "is-enabled", unit).ExitCode == 0;

    public bool IsActive(string unit) => Run("--user", "--quiet", "is-active", unit).ExitCode == 0;

    private static SystemctlResult Combined((int ExitCode, string Output, string Error) result) =>
        new(result.ExitCode, (result.Output + result.Error).Trim());

    private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Executable}.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{Executable} {arguments[^1]} did not finish within {Timeout.TotalSeconds:0} seconds.");
            }

            process.WaitForExit(); // Also waits for the redirected streams to drain.
            return (process.ExitCode, output.Result, error.Result.Trim());
        }
        catch (Win32Exception ex)
        {
            // No systemctl on PATH: not a systemd system. Reported like a failed call, with the reason.
            return (127, string.Empty, $"{Executable} could not be started: {ex.Message}");
        }
    }
}
