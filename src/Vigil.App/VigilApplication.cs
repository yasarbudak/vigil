using System.CommandLine;
using Microsoft.Extensions.Logging;
using Vigil.Commands;
using Vigil.Daemon;
using Vigil.Logging;

namespace Vigil;

/// <summary>
/// The entry points of the two executables, which are thin heads over this library: <c>vigil</c> (the console CLI,
/// every command, <c>run</c> included) and <c>vigild</c> (the windowless Windows daemon). Everything else in the
/// assembly is internal.
/// </summary>
public static class VigilApplication
{
    /// <summary>Exit code for a command line the daemon does not understand (the same code as an invalid configuration).</summary>
    private const int InvalidArguments = 2;

    /// <summary>Runs the <c>vigil</c> command line.</summary>
    public static async Task<int> RunCliAsync(string[] args)
    {
        var root = CommandFactory.Create(VigilPaths.ForCurrentUser());
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the daemon until <paramref name="cancellationToken"/> is cancelled or it is stopped over IPC
    /// (<c>vigil uninstall</c>). <paramref name="args"/> is what <c>vigild.exe</c> was started with: nothing, or
    /// <c>--data-dir &lt;path&gt;</c> as written by <c>vigil install</c>. Nobody sees the console of a windowless process, so
    /// a command line problem is written to <c>vigil.log</c> as well.
    /// </summary>
    public static Task<int> RunDaemonAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DaemonArguments.TryParse(args, out var dataDirectory, out var problem))
        {
            var message = $"Invalid arguments: {problem}";
            Console.Error.WriteLine($"vigil: {message}");
            LogStartupFailure(VigilPaths.ForCurrentUser(), message);
            return Task.FromResult(InvalidArguments);
        }

        var paths = dataDirectory is null ? VigilPaths.ForCurrentUser() : new VigilPaths(dataDirectory);
        return DaemonHost.RunAsync(paths, cancellationToken);
    }

    private static void LogStartupFailure(VigilPaths paths, string message)
    {
        try
        {
            paths.EnsureDataDirectory();
            using var log = new RotatingFileLoggerProvider(paths.LogPath);
            log.CreateLogger(typeof(VigilApplication).FullName!).LogError("Exiting with code {Code}: {Message}", InvalidArguments, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report it.
        }
    }
}
