using System.Diagnostics;
using System.Runtime.Versioning;

namespace Vigil.Platform.MacOS.LaunchAgent;

/// <summary>
/// Runs <c>/bin/launchctl</c> against <c>gui/&lt;uid&gt;</c>. Subcommands are the modern ones from <c>man launchctl</c>
/// (<c>bootstrap</c>, <c>bootout</c>, <c>print</c>). Arguments are passed as a list, never through a shell.
/// Only the exit status of <c>print</c> is used: its output is documented as not being API.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class Launchctl(uint uid) : ILaunchctl
{
    private const string Executable = "/bin/launchctl";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static Launchctl ForCurrentUser() => new(NativeMethods.GetUid());

    private string Domain => $"gui/{uid}";

    public bool IsLoaded(string label) => Run("print", $"{Domain}/{label}").Succeeded;

    public LaunchctlResult Bootstrap(string plistPath) => Run("bootstrap", Domain, plistPath);

    public LaunchctlResult Bootout(string label) => Run("bootout", $"{Domain}/{label}");

    private static LaunchctlResult Run(params string[] arguments)
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

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Executable} {arguments[0]} did not finish within {Timeout.TotalSeconds:0} seconds.");
        }

        process.WaitForExit(); // Also waits for the redirected streams to drain.
        return new LaunchctlResult(process.ExitCode, (output.Result + error.Result).Trim());
    }
}
