using Vigil.Ipc;

namespace Vigil.Tray;

/// <summary>The pause entries of the tray menu.</summary>
internal enum PauseChoice
{
    FifteenMinutes,
    OneHour,
    UntilTomorrow,
}

/// <summary>The IPC command behind each menu entry, so the menu holds no protocol knowledge.</summary>
internal static class TrayCommands
{
    public static IpcRequest Pause(PauseChoice choice) => choice switch
    {
        PauseChoice.FifteenMinutes => new IpcRequest(IpcCommand.Pause, TimeSpan.FromMinutes(15)),
        PauseChoice.OneHour => new IpcRequest(IpcCommand.Pause, TimeSpan.FromHours(1)),
        PauseChoice.UntilTomorrow => new IpcRequest(IpcCommand.PauseUntilTomorrow),
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown pause entry."),
    };

    public static string Label(PauseChoice choice) => choice switch
    {
        PauseChoice.FifteenMinutes => "Pause 15 minutes",
        PauseChoice.OneHour => "Pause 1 hour",
        PauseChoice.UntilTomorrow => "Pause until tomorrow",
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown pause entry."),
    };

    public static IpcRequest Resume() => new(IpcCommand.Resume);

    public static IpcRequest Status() => new(IpcCommand.Status);

    public static IpcRequest Reload() => new(IpcCommand.ReloadConfig);
}
