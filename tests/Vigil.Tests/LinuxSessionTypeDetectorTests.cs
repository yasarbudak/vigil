using Vigil.Platform.Linux;

namespace Vigil.Tests;

public class LinuxSessionTypeDetectorTests
{
    private static SessionDetection Detect(params (string Name, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Name, e => e.Value);
        return SessionTypeDetector.Detect(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void XDG_SESSION_TYPE_wayland_wins_even_when_DISPLAY_is_set_by_XWayland()
    {
        var result = Detect(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"));

        Assert.Equal(SessionType.Wayland, result.Type);
        Assert.Equal("XDG_SESSION_TYPE=wayland", result.Reason);
    }

    [Fact]
    public void XDG_SESSION_TYPE_x11_wins_over_a_stray_WAYLAND_DISPLAY()
    {
        Assert.Equal(SessionType.X11, Detect(("XDG_SESSION_TYPE", "x11"), ("WAYLAND_DISPLAY", "wayland-1")).Type);
    }

    [Theory]
    [InlineData("WAYLAND")]
    [InlineData(" wayland ")]
    public void The_type_is_case_insensitive_and_trimmed(string value)
    {
        Assert.Equal(SessionType.Wayland, Detect(("XDG_SESSION_TYPE", value)).Type);
    }

    [Theory]
    [InlineData("tty")]
    [InlineData("unspecified")]
    [InlineData("mir")]
    [InlineData("web")]
    [InlineData("")]
    public void Other_session_types_fall_through_to_the_display_variables(string type)
    {
        var result = Detect(("XDG_SESSION_TYPE", type), ("DISPLAY", ":1"));

        Assert.Equal(SessionType.X11, result.Type);
        Assert.Equal("DISPLAY=:1", result.Reason);
    }

    [Fact]
    public void Without_XDG_SESSION_TYPE_WAYLAND_DISPLAY_comes_before_DISPLAY()
    {
        Assert.Equal(SessionType.Wayland, Detect(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")).Type);
    }

    [Fact]
    public void Nothing_set_is_unknown_and_says_what_was_missing()
    {
        var result = Detect(("DISPLAY", "  "));

        Assert.Equal(SessionType.Unknown, result.Type);
        Assert.Contains("WAYLAND_DISPLAY", result.Reason, StringComparison.Ordinal);
    }
}
