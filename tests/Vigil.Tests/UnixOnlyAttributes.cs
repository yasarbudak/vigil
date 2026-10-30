using System.Runtime.CompilerServices;

namespace Vigil.Tests;

/// <summary>
/// A fact for a macOS or Linux feature (the app bundle, the LaunchAgent, the systemd unit, D-Bus addresses) that is
/// tested with Unix paths and file modes. Skipped on Windows, where those features do not exist; run on Linux and macOS.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = UnixOnly.Reason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="UnixOnlyFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class UnixOnlyTheoryAttribute : TheoryAttribute
{
    public UnixOnlyTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = UnixOnly.Reason;
        }
    }
}

internal static class UnixOnly
{
    public const string Reason = "Tests a macOS or Linux feature with Unix paths and file modes, which Windows does not have.";
}
