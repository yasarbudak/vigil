using Vigil.Platform.MacOS.LaunchAgent;

namespace Vigil.Tests;

public class LaunchAgentPlistTests
{
    private const string Exe = "/Applications/Vigil.app/Contents/MacOS/vigil";
    private const string Log = "/Users/me/Library/Application Support/Vigil/vigil.log";

    private static LaunchAgentSpec Spec(string exe = Exe, string log = Log, string? dataDirectory = null) =>
        new("io.github.yasarbudak.vigil", exe, log, "io.github.yasarbudak.vigil", dataDirectory);

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact]
    public void Plist_content_is_exactly_the_reviewed_golden_text()
    {
        var expected = Lines(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "\t<key>Label</key>",
            "\t<string>io.github.yasarbudak.vigil</string>",
            "\t<key>AssociatedBundleIdentifiers</key>",
            "\t<array>",
            "\t\t<string>io.github.yasarbudak.vigil</string>",
            "\t</array>",
            "\t<key>ProgramArguments</key>",
            "\t<array>",
            "\t\t<string>/Applications/Vigil.app/Contents/MacOS/vigil</string>",
            "\t\t<string>run</string>",
            "\t</array>",
            "\t<key>RunAtLoad</key>",
            "\t<true/>",
            "\t<key>KeepAlive</key>",
            "\t<dict>",
            "\t\t<key>Crashed</key>",
            "\t\t<true/>",
            "\t</dict>",
            "\t<key>ThrottleInterval</key>",
            "\t<integer>30</integer>",
            "\t<key>StandardOutPath</key>",
            "\t<string>/Users/me/Library/Application Support/Vigil/vigil.log</string>",
            "\t<key>StandardErrorPath</key>",
            "\t<string>/Users/me/Library/Application Support/Vigil/vigil.log</string>",
            "</dict>",
            "</plist>");

        Assert.Equal(expected, LaunchAgentPlist.Render(Spec()));
    }

    [Fact]
    public void A_custom_data_directory_is_passed_to_the_daemon_as_an_environment_variable()
    {
        var plist = LaunchAgentPlist.Render(Spec(dataDirectory: "/private/tmp/v g"));

        Assert.Contains(
            "\t<key>EnvironmentVariables</key>\n\t<dict>\n\t\t<key>VIGIL_DATA_DIR</key>\n\t\t<string>/private/tmp/v g</string>\n\t</dict>\n",
            plist,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Default_data_directory_adds_no_environment_variables()
    {
        Assert.DoesNotContain("EnvironmentVariables", LaunchAgentPlist.Render(Spec()), StringComparison.Ordinal);
    }

    [Fact]
    public void Restart_policy_is_crash_only_so_deliberate_error_exits_do_not_loop()
    {
        var plist = LaunchAgentPlist.Render(Spec());

        Assert.Contains("<key>Crashed</key>\n\t\t<true/>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("SuccessfulExit", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>\n\t<true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>ThrottleInterval</key>\n\t<integer>30</integer>", plist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Users/me/My Apps/Vigil.app/Contents/MacOS/vigil")]
    [InlineData("/Users/o'brien/\"q\"/Vigil.app/Contents/MacOS/vigil")]
    [InlineData("/Users/ünï/Vigil.app/Contents/MacOS/vigil")]
    public void Paths_with_spaces_quotes_and_non_ascii_are_written_verbatim(string exe)
    {
        var plist = LaunchAgentPlist.Render(Spec(exe, log: exe + ".log"));

        Assert.Contains($"\t\t<string>{exe}</string>\n\t\t<string>run</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_content_changes_with_the_executable_and_is_stable_for_the_same_input()
    {
        Assert.Equal(LaunchAgentPlist.Render(Spec()), LaunchAgentPlist.Render(Spec()));
        Assert.NotEqual(LaunchAgentPlist.Render(Spec()), LaunchAgentPlist.Render(Spec("/Applications/Other.app/Contents/MacOS/vigil")));
    }

    [Fact]
    public void Ampersand_and_angle_brackets_are_written_as_entities()
    {
        var plist = LaunchAgentPlist.Render(Spec("/Users/a&b/<x>/Vigil.app/Contents/MacOS/vigil"));

        Assert.Contains("<string>/Users/a&amp;b/&lt;x&gt;/Vigil.app/Contents/MacOS/vigil</string>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("a&b", plist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/vigil")]
    [InlineData("")]
    [InlineData("/Users/me/line\nbreak/Vigil.app/Contents/MacOS/vigil")]
    public void Relative_or_control_character_paths_are_rejected(string exe)
    {
        Assert.Throws<ArgumentException>(() => LaunchAgentPlist.Render(Spec(exe)));
    }
}
