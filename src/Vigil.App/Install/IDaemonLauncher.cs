using System.ComponentModel;
using System.Diagnostics;

namespace Vigil.Install;

internal readonly record struct DaemonLaunchResult(bool Succeeded, string? Detail = null);

/// <summary>Starts the daemon process. Behind an interface so tests never start a real process.</summary>
internal interface IDaemonLauncher
{
    /// <param name="executablePath">Absolute path of <c>vigild.exe</c>.</param>
    /// <param name="arguments">The command-line arguments, exactly as in the Run key value (see <c>DaemonArguments.BuildArguments</c>).</param>
    DaemonLaunchResult Start(string executablePath, string arguments);
}

/// <summary>
/// Starts <c>vigild.exe</c> with <see cref="Process.Start(ProcessStartInfo)"/> and does not wait for it, so it keeps
/// running after <c>vigil.exe</c> exits (a child is not stopped when its parent exits).
/// <c>UseShellExecute = false</c> makes the process be created directly from the executable (it is also the default on
/// .NET Core), and <c>CreateNoWindow = true</c> asks for no new window; <c>CreateNoWindow</c> is ignored when
/// <c>UseShellExecute</c> is true. <c>vigild.exe</c> is a WinExe (GUI subsystem) program, which never gets a console
/// window anyway (<c>docs/DEVELOPER.md</c>, "vigild.exe"); the flag keeps that true if the program were ever built
/// as a console one.
/// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute
/// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.createnowindow
/// </summary>
internal sealed class ProcessDaemonLauncher : IDaemonLauncher
{
    public DaemonLaunchResult Start(string executablePath, string arguments)
    {
        var info = new ProcessStartInfo(executablePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };

        try
        {
            // Process.Start returns null when no process was started.
            using var process = Process.Start(info);
            return process is null
                ? new DaemonLaunchResult(false, "Windows did not start a process.")
                : new DaemonLaunchResult(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new DaemonLaunchResult(false, ex.Message);
        }
    }
}
