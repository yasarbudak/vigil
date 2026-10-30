using Microsoft.Extensions.Logging;

namespace Vigil.Logging;

/// <summary>
/// Logger provider for the daemon's own <c>vigil.log</c>: all loggers share one <see cref="RotatingFileWriter"/>,
/// so the file has a single writer and rotation stays consistent. No reflection, safe for Native AOT.
/// </summary>
internal sealed class RotatingFileLoggerProvider(string path, long maxBytes = RotatingFileWriter.DefaultMaxBytes) : ILoggerProvider
{
    private readonly RotatingFileWriter _writer = new(path, maxBytes);

    public ILogger CreateLogger(string categoryName) => new RotatingFileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();
}
