using Vigil.Commands;
using Vigil.Core.Abstractions;
using Vigil.Platform;

namespace Vigil.Tests;

public class PermissionsCommandTests
{
    private sealed class Probe(bool granted) : IPermissionProbe
    {
        public bool CanPostEvents() => granted;
    }

    private sealed class Requester(bool granted) : IPermissionRequester
    {
        public int Calls { get; private set; }

        public bool RequestAccess()
        {
            Calls++;
            return granted;
        }
    }

    private static Func<PlatformOptions, PlatformServices> Factory(IPermissionProbe probe, IPermissionRequester? requester)
    {
        var fake = new TestPlatform();
        return _ => new PlatformServices("Fake OS", fake, fake, fake, probe, requester);
    }

    [Fact]
    public void A_platform_that_needs_no_permission_says_there_is_nothing_to_request_and_succeeds()
    {
        using var dir = new TempDataDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = PermissionsCommand.Request(dir.Paths, output, error, Factory(new Probe(true), requester: null));

        Assert.Equal(0, code);
        Assert.Contains("Nothing to request: Fake OS needs no permission", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void A_platform_without_a_requester_that_is_not_granted_still_fails()
    {
        using var dir = new TempDataDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = PermissionsCommand.Request(dir.Paths, output, error, Factory(new Probe(false), requester: null));

        Assert.Equal(1, code);
        Assert.Contains("no permission to request", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_platform_with_a_requester_asks_it_and_reports_the_result()
    {
        using var dir = new TempDataDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var requester = new Requester(granted: true);

        var code = PermissionsCommand.Request(dir.Paths, output, error, Factory(new Probe(false), requester));

        Assert.Equal(0, code);
        Assert.Equal(1, requester.Calls);
        Assert.Contains("is granted", output.ToString(), StringComparison.Ordinal);
    }
}
