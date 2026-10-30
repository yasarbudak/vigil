using System.Runtime.CompilerServices;

namespace Vigil.Tests;

/// <summary>
/// A fact for the macOS app-bundle and LaunchAgent feature, which only exists on macOS (<c>install</c> and
/// <c>uninstall</c> exit 1 elsewhere) and is tested with Unix paths and file modes. Skipped on Windows, still run on
/// Linux and macOS.
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
    public const string Reason = "The macOS app-bundle and LaunchAgent feature is not supported on Windows (Unix paths and file modes).";
}
