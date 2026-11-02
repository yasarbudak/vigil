using System.ComponentModel;
using System.Diagnostics;
using Vigil.Platform.Linux.DBus;

namespace Vigil.Tests;

/// <summary>
/// <see cref="TmdsDBusConnection"/> against a real <c>dbus-daemon</c>. Every other D-Bus test uses a fake connection, so
/// none of them checks the bytes that go on the wire; a method call header with the member and the signature swapped
/// passed all of them and was rejected by the real bus. These tests start a private session bus and call the bus itself.
/// They run on Linux only, and on Linux they fail (they do not skip) when <c>dbus-daemon</c> is not installed, because a
/// skip would hide exactly that kind of defect on a machine that cannot run them.
/// </summary>
public class LinuxDBusBusTests
{
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_without_a_body_gets_the_reply_of_the_bus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The D-Bus bus daemon is a Linux component here; the platform code under test only runs on Linux.");
        using var bus = PrivateBus.Start(Token);
        using var connection = await TmdsDBusConnection.ConnectAsync(bus.Address, Token);

        // GetId has an empty signature, so this exercises the destination, path, interface and member fields.
        var reply = await connection.CallAsync(DBusCall.Create(BusName, BusPath, BusName, "GetId"), Token);

        var id = Assert.IsType<DBusString>(Assert.Single(reply.Body));
        Assert.Matches("^[0-9a-f]{32}$", id.Value); // the bus id is 128 bits in hex
    }

    [Fact]
    public async Task A_call_with_a_body_carries_its_signature_and_gets_the_reply_of_the_bus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The D-Bus bus daemon is a Linux component here; the platform code under test only runs on Linux.");
        using var bus = PrivateBus.Start(Token);
        using var connection = await TmdsDBusConnection.ConnectAsync(bus.Address, Token);

        // GetNameOwner takes one string ("s"), so the header's signature field has to say so: with the member and the
        // signature swapped the bus gets no member and the signature "GetNameOwner", and closes the connection.
        var reply = await connection.CallAsync(DBusCall.Create(BusName, BusPath, BusName, "GetNameOwner", new DBusString(BusName)), Token);

        var owner = Assert.IsType<DBusString>(Assert.Single(reply.Body));
        Assert.Equal(BusName, owner.Value);
    }

    [Fact]
    public async Task An_error_reply_of_the_bus_is_reported_as_a_failed_call_and_the_connection_stays_usable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The D-Bus bus daemon is a Linux component here; the platform code under test only runs on Linux.");
        using var bus = PrivateBus.Start(Token);
        using var connection = await TmdsDBusConnection.ConnectAsync(bus.Address, Token);

        var failure = await Assert.ThrowsAsync<DBusCallFailedException>(() =>
            connection.CallAsync(DBusCall.Create(BusName, BusPath, BusName, "GetNameOwner", new DBusString("org.example.Nobody")), Token));
        Assert.Equal("org.freedesktop.DBus.Error.NameHasNoOwner", failure.ErrorName);

        var reply = await connection.CallAsync(DBusCall.Create(BusName, BusPath, BusName, "GetId"), Token);
        Assert.IsType<DBusString>(Assert.Single(reply.Body));
    }

    /// <summary>
    /// A <c>dbus-daemon</c> of its own on a socket in a short-named temporary directory (Unix socket paths are limited to
    /// about 100 bytes), so the test touches neither the login session bus nor any other state of the machine.
    /// </summary>
    private sealed class PrivateBus : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly Process _daemon;

        private PrivateBus(DirectoryInfo directory, Process daemon, string address)
        {
            _directory = directory;
            _daemon = daemon;
            Address = address;
        }

        public string Address { get; }

        public static PrivateBus Start(CancellationToken cancellationToken)
        {
            var directory = Directory.CreateTempSubdirectory("vg");
            Process? daemon = null;
            try
            {
                var address = $"unix:path={Path.Combine(directory.FullName, "bus")}";
                var configPath = Path.Combine(directory.FullName, "bus.conf");

                // The smallest session bus: one listen address, EXTERNAL authentication (the peer's uid, which is ours),
                // and every message allowed. See dbus-daemon(1), "CONFIGURATION FILE".
                File.WriteAllText(configPath, $"""
                    <busconfig>
                      <type>session</type>
                      <listen>{address}</listen>
                      <auth>EXTERNAL</auth>
                      <policy context="default">
                        <allow send_destination="*" eavesdrop="true"/>
                        <allow eavesdrop="true"/>
                        <allow own="*"/>
                      </policy>
                    </busconfig>
                    """);

                var startInfo = new ProcessStartInfo("dbus-daemon")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add($"--config-file={configPath}");
                startInfo.ArgumentList.Add("--nofork");
                startInfo.ArgumentList.Add("--print-address"); // written to stdout once the socket accepts connections

                try
                {
                    daemon = Process.Start(startInfo) ?? throw new InvalidOperationException("dbus-daemon did not start.");
                }
                catch (Win32Exception ex)
                {
                    Assert.Fail($"The D-Bus integration tests need the 'dbus-daemon' binary on PATH (Debian and Ubuntu: package 'dbus-daemon'; it is not installed here): {ex.Message}");
                }

                var printed = daemon.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(printed))
                {
                    throw new InvalidOperationException($"dbus-daemon did not report an address. Its error output: {ReadError(daemon)}");
                }

                return new PrivateBus(directory, daemon, address);
            }
            catch
            {
                Stop(daemon);
                directory.Delete(recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            Stop(_daemon);
            _directory.Delete(recursive: true);
        }

        private static string ReadError(Process daemon)
        {
            if (!daemon.HasExited)
            {
                daemon.Kill();
            }

            daemon.WaitForExit();
            return daemon.StandardError.ReadToEnd();
        }

        private static void Stop(Process? daemon)
        {
            if (daemon is null)
            {
                return;
            }

            try
            {
                if (!daemon.HasExited)
                {
                    daemon.Kill();
                    daemon.WaitForExit();
                }
            }
            finally
            {
                daemon.Dispose();
            }
        }
    }
}
