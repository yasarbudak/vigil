using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Activity;
using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Core.Presence;
using Vigil.Ipc;
using Vigil.Logging;
using Vigil.Platform;

namespace Vigil.Daemon;

/// <summary>Composition root of the daemon.</summary>
internal static class DaemonHost
{
    /// <param name="platformFactory">Replaces the OS implementations; used by tests so no real input is ever posted.</param>
    /// <param name="fileLog">
    /// The provider for <c>vigil.log</c> when the caller also needs it (<see cref="RunAsync"/> logs the way a run ends).
    /// The caller keeps ownership. Without it the host creates and disposes its own.
    /// </param>
    /// <param name="logToJournal">
    /// True when the output goes to the systemd journal, which keeps the log: the console logger then writes in the format
    /// journald understands (a priority prefix per line) and there is no <c>vigil.log</c>. Null decides from the process
    /// (<see cref="JournalDetection"/>); tests pass it explicitly.
    /// </param>
    /// <exception cref="ConfigurationException">config.json is unreadable, malformed or has invalid values.</exception>
    public static HostApplicationBuilder CreateBuilder(VigilPaths paths, Func<PlatformOptions, PlatformServices>? platformFactory = null, ILoggerProvider? fileLog = null, bool? logToJournal = null)
    {
        // Validated here, before anything is constructed: a bad config must stop the daemon before it can act.
        var settings = VigilConfiguration.LoadValidated(paths.ConfigPath);

        // DisableDefaults: no appsettings.json, no environment-variable or command-line configuration.
        // config.json in the data directory is the only source.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var journal = logToJournal ?? JournalDetection.IsAttachedToThisProcess();
        if (journal)
        {
            // The journal timestamps, rotates and caps the lines itself, and the priority prefix of the systemd formatter
            // is what lets `journalctl -p warning` filter them (sd-daemon(3): "<4>" is a warning). No vigil.log.
            builder.Logging.AddSystemdConsole();
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });

            // vigil.log is the daemon's own rotating log, for launchd and for `vigil run` alike. The console logger stays
            // so an interactive run still prints.
            if (fileLog is null)
            {
                builder.Services.AddSingleton<ILoggerProvider>(_ => new RotatingFileLoggerProvider(paths.LogPath));
            }
            else
            {
                builder.Logging.AddProvider(fileLog);
            }
        }

        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(settings.Vigil);
        services.AddSingleton(settings.MacOS);
        services.AddSingleton(settings.Windows);
        services.AddSingleton(settings.Linux);
        services.AddSingleton(settings.Platform with { DataDirectory = paths.DataDirectory });
        services.AddSingleton(provider => (platformFactory ?? PlatformServices.Create)(provider.GetRequiredService<PlatformOptions>()));

        services.AddSingleton<IActivityLog>(provider =>
            settings.Vigil.ActivityLog.Enabled
                ? new JsonlActivityLog(paths, settings.Vigil.ActivityLog, provider.GetRequiredService<ILogger<JsonlActivityLog>>())
                : new NullActivityLog());

        services.AddSingleton<ActivityLogRetention>();
        services.AddSingleton<PresenceEngine>();
        services.AddSingleton(provider =>
        {
            var platform = provider.GetRequiredService<PlatformServices>();
            return new PresenceCycle(
                provider.GetRequiredService<PresenceEngine>(),
                platform.Idle,
                platform.Injector,
                platform.Session,
                platform.Permissions,
                provider.GetRequiredService<TimeProvider>(),
                settings.Vigil);
        });

        services.AddSingleton<IIpcHandler, IpcHandler>();

        // Order matters: hosted services start one after another. The IPC server takes the single-instance lock,
        // so a second daemon fails here and the worker below never starts, never injects, never writes the log.
        services.AddHostedService(provider => new IpcServer(
            paths.SocketPath,
            provider.GetRequiredService<IIpcHandler>(),
            provider.GetRequiredService<ILogger<IpcServer>>()));
        services.AddHostedService<PresenceWorker>();

        return builder;
    }

    /// <summary>
    /// Runs the daemon. launchd discards stdout and stderr, so everything that ends a run is also written to
    /// <c>vigil.log</c>: a bad config, a failed start (such as a second instance), an unexpected exception and an
    /// unhandled exception on any thread. The console messages stay for interactive runs. Under systemd, where the
    /// journal keeps the log, the console messages and the runtime's own report of an unhandled exception are the record,
    /// so nothing is written twice and there is no file.
    /// </summary>
    public static async Task<int> RunAsync(VigilPaths paths, CancellationToken cancellationToken)
    {
        var journal = JournalDetection.IsAttachedToThisProcess();
        using var fileLog = journal ? null : new RotatingFileLoggerProvider(paths.LogPath);
        var logger = fileLog?.CreateLogger(typeof(DaemonHost).FullName!) ?? NullLogger.Instance;

        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                logger.LogCritical(exception, "Unhandled exception, the daemon is terminating.");
            }
            else
            {
                logger.LogCritical("Unhandled exception, the daemon is terminating: {Detail}", e.ExceptionObject);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;

        try
        {
            paths.EnsureDataDirectory();
            using var host = CreateBuilder(paths, fileLog: fileLog, logToJournal: journal).Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine($"vigil: {ex.Message}");
            logger.LogError("Exiting with code 2: {Message}", ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"vigil: {ex.Message}");
            logger.LogError(ex, "Exiting with code 1.");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && LogUnexpected(logger, ex))
        {
            throw; // Not reached: the filter only records the exception and lets it travel on to the caller.
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        }
    }

    /// <summary>Exception filter body: writes the exception to the log and returns false so it is not caught here.</summary>
    private static bool LogUnexpected(ILogger logger, Exception exception)
    {
        logger.LogCritical(exception, "Unexpected exception, the daemon is terminating.");
        return false;
    }
}
