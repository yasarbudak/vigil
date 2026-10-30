using System.Text;

namespace Vigil.Platform.MacOS.LaunchAgent;

/// <param name="Label">launchd job label; also the plist file name without extension.</param>
/// <param name="ExecutablePath">Absolute path of the executable inside the app bundle.</param>
/// <param name="BundleIdentifier">Shown in System Settings > Login Items as the owner of the job.</param>
/// <param name="DataDirectoryOverride">Set only when the user chose a non-default data directory; passed on to the daemon.</param>
public sealed record LaunchAgentSpec(
    string Label,
    string ExecutablePath,
    string BundleIdentifier,
    string? DataDirectoryOverride = null);

/// <summary>
/// Builds the per-user LaunchAgent property list. Pure: no file or launchctl access. It is deliberately write-only:
/// an existing file is compared byte for byte with what <see cref="Render"/> produces, so no XML parser is needed.
/// Key semantics are from <c>man launchd.plist</c>. There is no <c>StandardOutPath</c> or <c>StandardErrorPath</c>:
/// launchd then discards stdout and stderr, and the daemon writes and rotates its own <c>vigil.log</c>
/// (two writers on one file would break the rotation).
/// </summary>
public static class LaunchAgentPlist
{
    /// <summary>Minimum seconds between two launches (default is 10); slows a deterministic crash loop down.</summary>
    public const int ThrottleIntervalSeconds = 30;

    /// <summary>Name of the environment variable that moves the data directory (see <c>VigilPaths</c>).</summary>
    public const string DataDirectoryVariable = "VIGIL_DATA_DIR";

    /// <summary>
    /// Renders the plist. Restart policy: <c>RunAtLoad</c> starts the daemon at login, and <c>KeepAlive</c> with
    /// <c>Crashed=true</c> restarts it only when it died from a crash signal. <c>SuccessfulExit=false</c> would
    /// restart on every non-zero exit, including the deliberate ones (2 = invalid config, 1 = platform not supported
    /// or another instance running), which would loop. A normal stop (exit 0, or SIGTERM from <c>launchctl bootout</c>)
    /// is not a crash either.
    /// </summary>
    public static string Render(LaunchAgentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        RequireAbsolute(spec.ExecutablePath, nameof(spec.ExecutablePath));
        RequireText(spec.Label, nameof(spec.Label));
        RequireText(spec.BundleIdentifier, nameof(spec.BundleIdentifier));

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        xml.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        xml.Append("<plist version=\"1.0\">\n");
        xml.Append("<dict>\n");
        AppendString(xml, "Label", spec.Label);
        xml.Append("\t<key>AssociatedBundleIdentifiers</key>\n");
        xml.Append("\t<array>\n");
        xml.Append("\t\t<string>").Append(Escape(spec.BundleIdentifier)).Append("</string>\n");
        xml.Append("\t</array>\n");
        xml.Append("\t<key>ProgramArguments</key>\n");
        xml.Append("\t<array>\n");
        xml.Append("\t\t<string>").Append(Escape(spec.ExecutablePath)).Append("</string>\n");
        xml.Append("\t\t<string>run</string>\n");
        xml.Append("\t</array>\n");
        xml.Append("\t<key>RunAtLoad</key>\n");
        xml.Append("\t<true/>\n");
        xml.Append("\t<key>KeepAlive</key>\n");
        xml.Append("\t<dict>\n");
        xml.Append("\t\t<key>Crashed</key>\n");
        xml.Append("\t\t<true/>\n");
        xml.Append("\t</dict>\n");
        xml.Append("\t<key>ThrottleInterval</key>\n");
        xml.Append("\t<integer>").Append(ThrottleIntervalSeconds).Append("</integer>\n");
        if (spec.DataDirectoryOverride is { } dataDirectory)
        {
            RequireAbsolute(dataDirectory, nameof(spec.DataDirectoryOverride));
            xml.Append("\t<key>EnvironmentVariables</key>\n");
            xml.Append("\t<dict>\n");
            xml.Append("\t\t<key>").Append(DataDirectoryVariable).Append("</key>\n");
            xml.Append("\t\t<string>").Append(Escape(dataDirectory)).Append("</string>\n");
            xml.Append("\t</dict>\n");
        }

        xml.Append("</dict>\n");
        xml.Append("</plist>\n");
        return xml.ToString();
    }

    private static void AppendString(StringBuilder xml, string key, string value) =>
        xml.Append("\t<key>").Append(key).Append("</key>\n")
            .Append("\t<string>").Append(Escape(value)).Append("</string>\n");

    /// <summary>Escapes the three characters that are special in XML text. Control characters are rejected by the callers' checks.</summary>
    internal static string Escape(string value)
    {
        foreach (var c in value)
        {
            if (c < ' ')
            {
                throw new ArgumentException("Values in a property list must not contain control characters.", nameof(value));
            }
        }

        return value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static void RequireAbsolute(string path, string name)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
        {
            throw new ArgumentException("The path must be absolute.", name);
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", name);
        }
    }
}
