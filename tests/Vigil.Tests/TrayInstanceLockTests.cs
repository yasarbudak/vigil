using System.Runtime.Versioning;
using Vigil.Tray;

namespace Vigil.Tests;

public sealed class TrayInstanceLockTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_second_tray_in_the_same_data_directory_is_refused_until_the_first_ends()
    {
        var first = TrayInstanceLock.TryAcquire(_dir.Paths);
        Assert.NotNull(first);

        Assert.Null(TrayInstanceLock.TryAcquire(_dir.Paths));

        first.Dispose();
        using var again = TrayInstanceLock.TryAcquire(_dir.Paths);
        Assert.NotNull(again);
    }

    [Fact]
    public void Another_data_directory_has_its_own_tray()
    {
        using var other = new TempDataDirectory();
        using var first = TrayInstanceLock.TryAcquire(_dir.Paths);

        using var second = TrayInstanceLock.TryAcquire(other.Paths);

        Assert.NotNull(second);
    }

    [UnixOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public void The_lock_file_is_private_to_the_user()
    {
        using var held = TrayInstanceLock.TryAcquire(_dir.Paths);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dir.Paths.TrayLockPath));
    }
}
