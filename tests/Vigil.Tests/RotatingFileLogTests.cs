using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigil.Daemon;
using Vigil.Logging;

namespace Vigil.Tests;

public class RotatingFileLogTests
{
    private const int Cap = 200;

    private static string Line(char c, int length = 49) => new string(c, length); // 49 + line feed = 50 bytes

    [Fact]
    public void Lines_are_appended_until_the_cap_and_then_the_file_is_rotated()
    {
        using var dir = new TempDataDirectory();
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);

        foreach (var c in "abcd")
        {
            writer.WriteLine(Line(c)); // 4 x 50 = 200 bytes, exactly the cap
        }

        Assert.False(File.Exists(writer.BackupPath));
        Assert.Equal(200, new FileInfo(dir.Paths.LogPath).Length);

        writer.WriteLine(Line('e'));

        Assert.Equal(Line('a') + "\n" + Line('b') + "\n" + Line('c') + "\n" + Line('d') + "\n", SharedFile.ReadAllText(writer.BackupPath));
        Assert.Equal(Line('e') + "\n", SharedFile.ReadAllText(dir.Paths.LogPath));
    }

    [Fact]
    public void Only_one_backup_is_kept_and_it_is_overwritten()
    {
        using var dir = new TempDataDirectory();
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);
        string Numbered(int i) => $"line{i:D2}".PadRight(49, '-');

        for (var i = 0; i < 40; i++)
        {
            writer.WriteLine(Numbered(i)); // 40 x 50 = 2000 bytes written, ten times the cap
        }

        Assert.Equal(["vigil.log", "vigil.log.1"], Directory.GetFiles(dir.Paths.DataDirectory).Select(Path.GetFileName).Order().ToArray());
        Assert.True(new FileInfo(dir.Paths.LogPath).Length <= Cap);
        Assert.True(new FileInfo(writer.BackupPath).Length <= Cap);

        // Backup then live file form the unbroken tail of what was written: the newest lines survive, the oldest are gone.
        var kept = SharedFile.ReadAllLines(writer.BackupPath).Concat(SharedFile.ReadAllLines(dir.Paths.LogPath)).ToArray();
        Assert.True(kept.Length < 40);
        Assert.Equal(Enumerable.Range(40 - kept.Length, kept.Length).Select(Numbered), kept);
    }

    [Fact]
    public void An_oversized_file_from_before_is_rotated_when_the_writer_starts()
    {
        using var dir = new TempDataDirectory();
        File.WriteAllText(dir.Paths.LogPath, new string('x', Cap + 1));
        File.WriteAllText(dir.Paths.LogPath + ".1", "older backup");

        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);
        writer.WriteLine("fresh");

        Assert.Equal(new string('x', Cap + 1), SharedFile.ReadAllText(writer.BackupPath));
        Assert.Equal("fresh\n", SharedFile.ReadAllText(dir.Paths.LogPath));
    }

    [Fact]
    public void A_file_within_the_cap_is_appended_to_not_rotated_at_start()
    {
        using var dir = new TempDataDirectory();
        File.WriteAllText(dir.Paths.LogPath, "old\n");

        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);
        writer.WriteLine("new");

        Assert.Equal("old\nnew\n", SharedFile.ReadAllText(dir.Paths.LogPath));
        Assert.False(File.Exists(writer.BackupPath));
    }

    [Fact]
    public void New_files_are_private_to_the_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDataDirectory();
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);
        for (var i = 0; i < 5; i++)
        {
            writer.WriteLine(Line('a')); // the fifth line rotates, so both the new live file and the backup are checked
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(dir.Paths.LogPath));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(writer.BackupPath));
    }

    [Fact]
    public void The_file_can_be_read_while_the_writer_holds_it_open()
    {
        using var dir = new TempDataDirectory();
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);

        writer.WriteLine("visible");

        using var reader = new FileStream(dir.Paths.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal("visible\n", new StreamReader(reader).ReadToEnd());
    }

    [Fact]
    public async Task Concurrent_writers_produce_only_intact_lines()
    {
        using var dir = new TempDataDirectory();
        const int threads = 8;
        const int perThread = 200;
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, 64 * 1024);

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    writer.WriteLine($"t{t}-{i}-" + new string('z', 40));
                }
            },
            TestContext.Current.CancellationToken)));

        var lines = SharedFile.ReadAllLines(dir.Paths.LogPath).Concat(SharedFile.ReadAllLines(writer.BackupPath)).ToArray();
        // 1600 lines of about 50 bytes exceed 64 KiB, so rotation ran during the writes as well.
        Assert.True(File.Exists(writer.BackupPath));
        Assert.All(lines, l => Assert.Matches(@"^t[0-7]-\d+-z{40}$", l));
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Fact]
    public void An_unwritable_log_never_throws_and_recovers_when_the_problem_goes_away()
    {
        using var dir = new TempDataDirectory();
        var path = Path.Combine(dir.Paths.DataDirectory, "missing", "vigil.log");

        using var writer = new RotatingFileWriter(path, Cap); // the directory does not exist: opening fails
        writer.WriteLine("dropped");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer.WriteLine("kept");

        Assert.Equal("kept\n", SharedFile.ReadAllText(path));
    }

    [Fact]
    public void A_log_path_that_is_a_directory_does_not_throw()
    {
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.Paths.LogPath);

        using var provider = new RotatingFileLoggerProvider(dir.Paths.LogPath);

        var exception = Record.Exception(() => provider.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "still alive"));
        Assert.Null(exception);
    }

    [Fact]
    public void A_failing_rotation_does_not_throw_and_logging_continues_when_it_can_succeed_again()
    {
        using var dir = new TempDataDirectory();
        using var writer = new RotatingFileWriter(dir.Paths.LogPath, Cap);
        for (var i = 0; i < 4; i++)
        {
            writer.WriteLine(Line('a'));
        }

        Directory.CreateDirectory(writer.BackupPath); // File.Move cannot replace a directory
        var exception = Record.Exception(() => writer.WriteLine(Line('b')));
        Assert.Null(exception);

        Directory.Delete(writer.BackupPath);
        writer.WriteLine(Line('c'));

        Assert.Equal(Line('c') + "\n", SharedFile.ReadAllText(dir.Paths.LogPath));
    }

    [Fact]
    public void Entries_use_the_console_layout_and_include_exceptions_on_one_line()
    {
        using var dir = new TempDataDirectory();
        using var provider = new RotatingFileLoggerProvider(dir.Paths.LogPath);
        var logger = provider.CreateLogger("Vigil.Daemon.PresenceWorker");

        logger.LogInformation("State: {State}", "Injected");
        logger.LogWarning("two\nlines");
        logger.LogError(new InvalidOperationException("boom"), "failed");

        var lines = SharedFile.ReadAllLines(dir.Paths.LogPath);
        Assert.Equal(3, lines.Length);
        Assert.Matches(new Regex(@"^\d\d:\d\d:\d\d info: Vigil\.Daemon\.PresenceWorker\[0\] State: Injected$"), lines[0]);
        Assert.Matches(new Regex(@"^\d\d:\d\d:\d\d warn: Vigil\.Daemon\.PresenceWorker\[0\] two lines$"), lines[1]);
        Assert.Matches(new Regex(@"^\d\d:\d\d:\d\d fail: Vigil\.Daemon\.PresenceWorker\[0\] failed System\.InvalidOperationException: boom"), lines[2]);
    }

    [Fact]
    public async Task A_config_error_is_printed_returns_exit_code_2_and_reaches_the_log_file()
    {
        using var dir = new TempDataDirectory();
        dir.WriteConfig("""{ "Vigil": { "Threshold": "00:00:00" } }""");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        int code;
        try
        {
            code = await DaemonHost.RunAsync(dir.Paths, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(2, code);
        Assert.Contains("Threshold", error.ToString(), StringComparison.Ordinal);
        var log = SharedFile.ReadAllText(dir.Paths.LogPath);
        Assert.Contains("fail: Vigil.Daemon.DaemonHost[0] Exiting with code 2: Invalid configuration in", log, StringComparison.Ordinal);
        Assert.Contains("Threshold", log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_daemon_host_writes_its_log_lines_to_the_file()
    {
        using var dir = new TempDataDirectory();
        using var host = DaemonHost.CreateBuilder(dir.Paths, new TestPlatform().Factory).Build();

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vigil.Test").LogInformation("hello log");

        Assert.Contains("info: Vigil.Test[0] hello log", SharedFile.ReadAllText(dir.Paths.LogPath), StringComparison.Ordinal);
    }
}
