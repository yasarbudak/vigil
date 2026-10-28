using System.CommandLine;
using Vigil.Configuration;
using Vigil.Core;
using Vigil.Daemon;
using Vigil.Ipc;
using Vigil.Platform;

namespace Vigil.Commands;

internal static class CommandFactory
{
    public static RootCommand Create(VigilPaths paths)
    {
        var root = new RootCommand("Vigil keeps Teams and Slack from showing you as Away, only when you are really idle.");
        root.Subcommands.Add(Run(paths));
        root.Subcommands.Add(Status(paths));
        root.Subcommands.Add(Pause(paths));
        root.Subcommands.Add(Resume(paths));
        root.Subcommands.Add(Diag(paths));
        root.Subcommands.Add(Permissions(paths));
        root.Subcommands.Add(Install(paths));
        root.Subcommands.Add(Uninstall(paths));
        return root;
    }

    private static Command Run(VigilPaths paths)
    {
        var command = new Command("run", "Run the daemon in the foreground. Stop it with Ctrl+C.");
        command.SetAction((_, cancellationToken) => DaemonHost.RunAsync(paths, cancellationToken));
        return command;
    }

    private static Command Status(VigilPaths paths)
    {
        var command = new Command("status", "Show what the running daemon is doing.");
        command.SetAction((_, cancellationToken) =>
            DaemonClientCommand.ExecuteAsync(paths, new IpcRequest(IpcCommand.Status), cancellationToken));
        return command;
    }

    private static Command Pause(VigilPaths paths)
    {
        var duration = new Argument<string?>("duration")
        {
            Description = "How long to pause, e.g. 15m, 1h, 1h30m.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var untilTomorrow = new Option<bool>("--until-tomorrow")
        {
            Description = "Pause until the start of tomorrow's schedule window (00:00 with the default schedule).",
        };

        var command = new Command("pause", "Pause Vigil for a while.");
        command.Arguments.Add(duration);
        command.Options.Add(untilTomorrow);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var text = parseResult.GetValue(duration);
            var tomorrow = parseResult.GetValue(untilTomorrow);

            if (tomorrow == (text is not null))
            {
                await Console.Error.WriteLineAsync("vigil: give either a duration (e.g. 15m) or --until-tomorrow.").ConfigureAwait(false);
                return 1;
            }

            if (tomorrow)
            {
                return await DaemonClientCommand.ExecuteAsync(paths, new IpcRequest(IpcCommand.PauseUntilTomorrow), cancellationToken).ConfigureAwait(false);
            }

            if (!DurationParser.TryParse(text, out var parsed))
            {
                await Console.Error.WriteLineAsync($"vigil: '{text}' is not a valid duration. Use forms like 30s, 15m, 1h, 1h30m.").ConfigureAwait(false);
                return 1;
            }

            return await DaemonClientCommand.ExecuteAsync(paths, new IpcRequest(IpcCommand.Pause, parsed), cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static Command Resume(VigilPaths paths)
    {
        var command = new Command("resume", "End a manual pause.");
        command.SetAction((_, cancellationToken) =>
            DaemonClientCommand.ExecuteAsync(paths, new IpcRequest(IpcCommand.Resume), cancellationToken));
        return command;
    }

    private static Command Diag(VigilPaths paths)
    {
        var command = new Command("diag", "Print platform, idle time, permission and configuration. Read only; works without the daemon.");
        command.SetAction(_ => DiagCommand.Execute(paths, Console.Out));
        return command;
    }

    private static Command Permissions(VigilPaths paths)
    {
        var request = new Command("request", "Ask macOS for permission to post input events (may show a system prompt).");
        request.SetAction(_ => PermissionsCommand.Request(paths, Console.Out, Console.Error));

        var command = new Command("permissions", "Manage the OS permission Vigil needs.");
        command.Subcommands.Add(request);
        return command;
    }

    private static Command Install(VigilPaths paths)
    {
        var force = new Option<bool>("--force")
        {
            Description = "Replace an existing autostart entry that points at a different executable.",
        };
        var command = new Command(
            "install",
            "Start Vigil at login (macOS LaunchAgent). Only runs when you call it; must be run from inside Vigil.app.");
        command.Options.Add(force);
        command.SetAction(parseResult =>
            AutostartCommand.Install(paths, parseResult.GetValue(force), Console.Out, Console.Error));
        return command;
    }

    private static Command Uninstall(VigilPaths paths)
    {
        var purge = new Option<bool>("--purge")
        {
            Description = "Also delete the Vigil data directory (config, activity log, daemon log).",
        };
        var command = new Command("uninstall", "Stop Vigil and remove its autostart entry. Keeps the data directory unless --purge is given.");
        command.Options.Add(purge);
        command.SetAction(parseResult =>
            AutostartCommand.Uninstall(paths, parseResult.GetValue(purge), Console.Out, Console.Error));
        return command;
    }
}
