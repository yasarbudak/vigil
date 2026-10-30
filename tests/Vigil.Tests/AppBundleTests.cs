using Vigil.Platform.MacOS.LaunchAgent;

namespace Vigil.Tests;

public class AppBundleTests
{
    [UnixOnlyTheory]
    [InlineData("/Applications/Vigil.app/Contents/MacOS/vigil", "/Applications/Vigil.app")]
    [InlineData("/Users/me/Applications/Vigil.app/Contents/MacOS/vigil", "/Users/me/Applications/Vigil.app")]
    [InlineData("/Users/me/My Apps/Vigil.app/Contents/MacOS/vigil", "/Users/me/My Apps/Vigil.app")]
    [InlineData("/tmp/a.app/b.app/Contents/MacOS/x", "/tmp/a.app/b.app")]
    public void Executables_inside_an_app_bundle_are_recognised(string executable, string bundle)
    {
        Assert.True(AppBundle.TryGetBundlePath(executable, out var found));
        Assert.Equal(bundle, found);
    }

    [UnixOnlyTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vigil")]
    [InlineData("Vigil.app/Contents/MacOS/vigil")]
    [InlineData("/usr/local/bin/vigil")]
    [InlineData("/Users/me/src/vigil/src/Vigil/bin/Release/net10.0/osx-x64/publish/vigil")]
    [InlineData("/Applications/Vigil.app/Contents/vigil")]
    [InlineData("/Applications/Vigil.app/Contents/Resources/vigil")]
    [InlineData("/Applications/Vigil.app/Contents/MacOS/")]
    [InlineData("/Applications/Vigil/Contents/MacOS/vigil")]
    [InlineData("/Applications/.app/Contents/MacOS/vigil")]
    [InlineData("/Applications/Vigil.app/Contents/MacOS/sub/vigil")]
    [InlineData("/Contents/MacOS/vigil")]
    public void Everything_else_is_not_a_bundle_executable(string? executable)
    {
        Assert.False(AppBundle.TryGetBundlePath(executable, out var found));
        Assert.Equal(string.Empty, found);
    }
}
