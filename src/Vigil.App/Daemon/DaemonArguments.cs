namespace Vigil.Daemon;

/// <summary>
/// The command line of the Windows daemon, <c>vigild.exe</c>, in both directions: <see cref="Build"/> renders what
/// <c>vigil install</c> stores in the Run key, and <see cref="TryParse"/> reads what the daemon is started with. The
/// only argument is <c>--data-dir &lt;path&gt;</c>, the Windows counterpart of the <c>VIGIL_DATA_DIR</c> entry the
/// macOS LaunchAgent carries: a daemon started at sign-in does not see the variable that was set in the terminal
/// where <c>vigil install</c> ran. Without the argument the daemon uses <c>VIGIL_DATA_DIR</c> or the default directory.
/// </summary>
internal static class DaemonArguments
{
    public const string DataDirectoryOption = "--data-dir";

    /// <summary>
    /// The Run key data is a command line "no longer than 260 characters"
    /// (https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).
    /// </summary>
    public const int MaxCommandLineLength = 260;

    /// <summary>
    /// <c>"&lt;executable&gt;"</c>, followed by <c>--data-dir "&lt;directory&gt;"</c> when a directory is given. Both are quoted, so
    /// a path with spaces survives. Windows file names cannot contain a double quote, which is also what makes the
    /// quoting safe: a path that has one is rejected rather than escaped.
    /// </summary>
    /// <exception cref="ArgumentException">A path is not absolute or has a quote or control character, or the result is too long.</exception>
    public static string Build(string executablePath, string? dataDirectory)
    {
        var commandLine = Quote(executablePath, nameof(executablePath));
        var arguments = BuildArguments(dataDirectory);
        if (arguments.Length > 0)
        {
            commandLine += " " + arguments;
        }

        return commandLine.Length <= MaxCommandLineLength
            ? commandLine
            : throw new ArgumentException($"The command line is {commandLine.Length} characters long, more than the {MaxCommandLineLength} that a Run key value may have. Install Vigil in a folder with a shorter path.", nameof(executablePath));
    }

    /// <summary>
    /// Only the arguments part of <see cref="Build"/>: <c>--data-dir "&lt;directory&gt;"</c>, or an empty string without a
    /// directory. <c>vigil install</c> starts the daemon with this very string, so the process it starts and the Run
    /// key value cannot disagree.
    /// </summary>
    /// <exception cref="ArgumentException">The directory is not absolute or has a quote or control character.</exception>
    public static string BuildArguments(string? dataDirectory) =>
        dataDirectory is null ? string.Empty : $"{DataDirectoryOption} {Quote(dataDirectory, nameof(dataDirectory))}";

    /// <summary>Accepts no argument, or exactly <c>--data-dir</c> and an absolute path.</summary>
    /// <param name="problem">What is wrong when this returns false.</param>
    public static bool TryParse(IReadOnlyList<string> arguments, out string? dataDirectory, out string problem)
    {
        dataDirectory = null;
        problem = string.Empty;

        if (arguments.Count == 0)
        {
            return true;
        }

        if (arguments.Count != 2 || arguments[0] != DataDirectoryOption)
        {
            problem = $"expected no arguments or '{DataDirectoryOption} <path>', got: {string.Join(' ', arguments)}";
            return false;
        }

        if (!Path.IsPathFullyQualified(arguments[1]))
        {
            problem = $"'{DataDirectoryOption}' needs an absolute path, got '{arguments[1]}'.";
            return false;
        }

        dataDirectory = arguments[1];
        return true;
    }

    /// <summary>
    /// Wraps a path in double quotes the way the Windows command-line parser reads them: backslashes are literal
    /// except in front of a double quote, so the ones at the very end of the path must be doubled or they would escape
    /// the closing quote (<c>"D:\"</c> would read as <c>D:"</c>).
    /// https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments
    /// </summary>
    private static string Quote(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(c => c == '"' || char.IsControl(c)))
        {
            throw new ArgumentException($"'{path}' is not an absolute path without quotes or control characters.", name);
        }

        var trailing = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailing) + "\"";
    }
}
