using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigil.Logging;

/// <summary>
/// Writes one line per entry in the layout of the single-line console logger
/// (<c>12:15:24 info: Vigil.Daemon.PresenceWorker[0] State: Injected</c>), with the exception text appended.
/// Line breaks inside a message or exception are replaced by spaces, so every entry stays one intact line.
/// </summary>
internal sealed class RotatingFileLogger(string category, RotatingFileWriter writer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var line = new StringBuilder();
        line.Append(TimeProvider.System.GetLocalNow().ToString("HH:mm:ss ", CultureInfo.InvariantCulture))
            .Append(LevelName(logLevel)).Append(": ")
            .Append(category).Append('[').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        AppendText(line, formatter(state, exception));
        if (exception is not null)
        {
            AppendText(line, exception.ToString());
        }

        writer.WriteLine(line.ToString());
    }

    private static void AppendText(StringBuilder line, string text)
    {
        if (text.Length > 0)
        {
            line.Append(' ').Append(text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' '));
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}
