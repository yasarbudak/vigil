using Vigil.Daemon;

namespace Vigil.Tests;

public class DaemonArgumentsTests
{
    private static readonly string Executable = Path.Combine(Path.GetTempPath(), "Vigil App", "vigild.exe");

    [Fact]
    public void The_executable_alone_is_one_quoted_path()
    {
        Assert.Equal($"\"{Executable}\"", DaemonArguments.Build(Executable, dataDirectory: null));
    }

    [Fact]
    public void A_data_directory_is_added_as_a_second_quoted_argument()
    {
        var directory = Path.Combine(Path.GetTempPath(), "My Data");

        Assert.Equal($"\"{Executable}\" --data-dir \"{directory}\"", DaemonArguments.Build(Executable, directory));
    }

    [Fact]
    public void Trailing_backslashes_are_doubled_so_they_cannot_escape_the_closing_quote()
    {
        // The Windows command-line parser reads backslash-quote as a literal quote; two backslashes then a quote as one
        // backslash and the end of the argument. The rule is about the characters, so it is checked with either style of path.
        var directory = OperatingSystem.IsWindows() ? @"C:\Vigil Data\" : "/vigil data\\";
        var doubled = OperatingSystem.IsWindows() ? @"C:\Vigil Data\\" : "/vigil data\\\\";

        Assert.EndsWith($" --data-dir \"{doubled}\"", DaemonArguments.Build(Executable, directory), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vigild.exe")]
    [InlineData("relative\\vigild.exe")]
    [InlineData("")]
    public void A_path_that_is_not_absolute_is_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => DaemonArguments.Build(path, null));
    }

    [Fact]
    public void A_path_with_a_quote_or_a_control_character_is_rejected_not_escaped()
    {
        Assert.Throws<ArgumentException>(() => DaemonArguments.Build(Executable.Replace("Vigil App", "Vigil \"App\"", StringComparison.Ordinal), null));
        Assert.Throws<ArgumentException>(() => DaemonArguments.Build(Executable, Path.GetTempPath() + "a\nb"));
    }

    [Fact]
    public void A_command_line_longer_than_260_characters_is_rejected_with_an_explanation()
    {
        var longDirectory = Path.Combine(Path.GetTempPath(), new string('d', 200));

        var error = Assert.Throws<ArgumentException>(() => DaemonArguments.Build(Path.Combine(longDirectory, "vigild.exe"), Path.Combine(Path.GetTempPath(), new string('e', 100))));

        Assert.Contains("260", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_of_exactly_260_characters_is_accepted()
    {
        var prefix = Path.GetTempPath();
        var suffix = Path.DirectorySeparatorChar + "vigild.exe";
        var padding = DaemonArguments.MaxCommandLineLength - 2 - prefix.Length - suffix.Length; // 2 quotes
        var path = prefix + new string('p', padding) + suffix;

        Assert.Equal(DaemonArguments.MaxCommandLineLength, DaemonArguments.Build(path, null).Length);
    }

    [Fact]
    public void No_arguments_means_no_data_directory()
    {
        Assert.True(DaemonArguments.TryParse([], out var directory, out _));
        Assert.Null(directory);
    }

    [Fact]
    public void The_data_dir_option_with_an_absolute_path_is_read()
    {
        var directory = Path.Combine(Path.GetTempPath(), "My Data");

        Assert.True(DaemonArguments.TryParse(["--data-dir", directory], out var parsed, out _));
        Assert.Equal(directory, parsed);
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir", "relative")]
    [InlineData("--other", "x")]
    [InlineData("run")]
    [InlineData("--data-dir", "a", "b")]
    public void Anything_else_is_rejected_with_the_problem_named(params string[] arguments)
    {
        Assert.False(DaemonArguments.TryParse(arguments, out var directory, out var problem));
        Assert.Null(directory);
        Assert.NotEmpty(problem);
    }
}
