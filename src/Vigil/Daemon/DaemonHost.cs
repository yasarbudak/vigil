using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Activity;
using Vigil.Configuration;
using Vigil.Core.Abstractions;
using Vigil.Core.Presence;
using Vigil.Ipc;
using Vigil.Platform;
using Vigil.Platform.MacOS;

namespace Vigil.Daemon;

/// <summary>Composition root of the daemon.</summary>
internal static class DaemonHost
{
    /// <param name="platformFactory">Replaces the OS implementations; used by tests so no real input is ever posted.</param>
    /// <exception cref="ConfigurationException">config.json is unreadable, malformed or has invalid values.</exception>
    public static HostApplicationBuilder CreateBuilder(VigilPaths paths, Func<MacOSOptions, PlatformServices>? platformFactory = null)
    {
        // Validated here, before anything is constructed: a bad config must stop the daemon before it can act.
        var settings = VigilConfiguration.LoadValidated(paths.ConfigPath);

        // DisableDefaults: no appsettings.json, no environment-variable or command-line configuration.
        // config.json in the data directory is the only source.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });

        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(settings.Vigil);
        services.AddSingleton(settings.MacOS);
        services.AddSingleton(provider => (platformFactory ?? PlatformServices.Create)(provider.GetRequiredService<MacOSOptions>()));

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

    public static async Task<int> RunAsync(VigilPaths paths, CancellationToken cancellationToken)
    {
        try
        {
            paths.EnsureDataDirectory();
            using var host = CreateBuilder(paths).Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine($"vigil: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"vigil: {ex.Message}");
            return 1;
        }
    }
}
