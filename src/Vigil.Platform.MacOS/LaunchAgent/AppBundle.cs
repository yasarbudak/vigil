namespace Vigil.Platform.MacOS.LaunchAgent;

/// <summary>
/// macOS ties the permission to post input events to the app bundle (its code signature and bundle identifier),
/// so a LaunchAgent must start the executable inside an <c>.app</c> bundle, not a loose binary.
/// </summary>
public static class AppBundle
{
    private const string BundleExtension = ".app";
    private const string ContentsDirectory = "Contents";
    private const string ExecutableDirectory = "MacOS";

    /// <summary>
    /// True when <paramref name="executablePath"/> is an absolute path of the form
    /// <c>&lt;dir&gt;/X.app/Contents/MacOS/&lt;name&gt;</c>; <paramref name="bundlePath"/> is then <c>&lt;dir&gt;/X.app</c>.
    /// Purely lexical: nothing is read from disk and symbolic links are not resolved.
    /// </summary>
    public static bool TryGetBundlePath(string? executablePath, out string bundlePath)
    {
        bundlePath = string.Empty;
        if (string.IsNullOrEmpty(executablePath) || !executablePath.StartsWith('/'))
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        var contents = Path.GetDirectoryName(executableDirectory);
        var bundle = Path.GetDirectoryName(contents);
        if (string.IsNullOrEmpty(Path.GetFileName(executablePath))
            || Path.GetFileName(executableDirectory) != ExecutableDirectory
            || Path.GetFileName(contents) != ContentsDirectory
            || bundle is null)
        {
            return false;
        }

        var bundleName = Path.GetFileName(bundle);
        if (bundleName.Length <= BundleExtension.Length || !bundleName.EndsWith(BundleExtension, StringComparison.Ordinal))
        {
            return false;
        }

        bundlePath = bundle;
        return true;
    }

    /// <summary>The bundle's Info.plist, which the OS reads for the bundle identifier.</summary>
    public static string InfoPlistPath(string bundlePath) => Path.Combine(bundlePath, ContentsDirectory, "Info.plist");
}
