using Vigil.Install;
using Vigil.Platform.Windows.Autostart;

namespace Vigil.Tests;

/// <summary>
/// The Windows install and uninstall flow with a fake registry and a fake daemon: the real HKCU Run key is never
/// read or written and no daemon is touched. Runs on every OS; only the file permission details differ.
/// </summary>
public sealed class RunKeyManagerTests : IDisposable
{
    private const string Name = "Vigil";

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, RunKeyValue> Values { get; } = [];

        public List<string> Calls { get; } = [];

        public RunKeyValue? Read(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public void Write(string name, string commandLine)
        {
            Calls.Add($"write {name}");
            Values[name] = new RunKeyValue(commandLine);
        }

        public bool Delete(string name)
        {
            Calls.Add($"delete {name}");
            return Values.Remove(name);
        }
    }

    private sealed class FakeDaemon : IDaemonStopper
    {
        public DaemonStopResult Result { get; set; } = new(DaemonStopOutcome.NotRunning);

        public int StopCalls { get; private set; }

        public DaemonStopResult Stop()
        {
            StopCalls++;
            return Result;
        }
    }

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");
    private readonly FakeRunKey _registry = new();
    private readonly FakeDaemon _daemon = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _dataDirectory;

    public RunKeyManagerTests()
    {
        _dataDirectory = Path.Combine(_root.FullName, "Vigil");
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _output.Dispose();
        _error.Dispose();
    }

    /// <summary>Makes a folder with vigil.exe and (unless told not to) vigild.exe in it, like the unzipped release; returns the path of vigil.exe.</summary>
    private string MakeInstallFolder(string name = "App", bool withDaemon = true)
    {
        var folder = Path.Combine(_root.FullName, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "vigil.exe"), "x");
        if (withDaemon)
        {
            File.WriteAllText(Path.Combine(folder, "vigild.exe"), "x");
        }

        return Path.Combine(folder, "vigil.exe");
    }

    private static string DaemonCommand(string vigilExe) => $"\"{Path.Combine(Path.GetDirectoryName(vigilExe)!, "vigild.exe")}\"";

    private RunKeyManager Manager(string? dataDirectory = null, string? defaultDirectory = null) =>
        new(_registry, _daemon, new VigilPaths(dataDirectory ?? _dataDirectory), defaultDirectory ?? _dataDirectory);

    [Fact]
    public void Fresh_install_writes_one_quoted_value_pointing_at_the_daemon_and_prepares_the_log()
    {
        var exe = MakeInstallFolder();

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal([$"write {Name}"], _registry.Calls);
        Assert.Equal(DaemonCommand(exe), _registry.Values[Name].Text);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "vigil.log")));
        Assert.Contains("next sign-in", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_dataDirectory, "vigil.log"), _output.ToString(), StringComparison.Ordinal);
        Assert.Contains(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\Vigil", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _daemon.StopCalls); // Installing never stops or starts a daemon.
    }

    [Fact]
    public void Install_without_a_current_executable_path_is_refused()
    {
        Assert.Equal(1, Manager().Install(null, force: false, _output, _error));
        Assert.Equal(1, Manager().Install("vigil.exe", force: false, _output, _error));
        Assert.Empty(_registry.Calls);
    }

    [Fact]
    public void Install_without_the_daemon_next_to_vigil_exe_is_refused_and_changes_nothing()
    {
        var exe = MakeInstallFolder(withDaemon: false);

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("vigild.exe was not found", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_registry.Calls);
        Assert.False(Directory.Exists(_dataDirectory));
    }

    [Fact]
    public void Install_with_a_command_line_over_the_limit_is_refused_with_the_reason()
    {
        var exe = MakeInstallFolder(new string('d', 255));

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("260", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_registry.Calls);
    }

    [Fact]
    public void Install_again_with_the_same_executable_is_a_no_op()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        _registry.Calls.Clear();

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_registry.Calls);
        Assert.Contains("Already installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_pointing_elsewhere_is_refused_without_force_and_leaves_the_value_alone()
    {
        var first = MakeInstallFolder("One");
        var second = MakeInstallFolder("Two");
        Manager().Install(first, force: false, _output, _error);
        _registry.Calls.Clear();

        var code = Manager().Install(second, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("--force", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(DaemonCommand(first), _registry.Values[Name].Text);
        Assert.Empty(_registry.Calls);
    }

    [Fact]
    public void Install_with_force_replaces_the_value()
    {
        var first = MakeInstallFolder("One");
        var second = MakeInstallFolder("Two");
        Manager().Install(first, force: false, _output, _error);

        var code = Manager().Install(second, force: true, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(DaemonCommand(second), _registry.Values[Name].Text);
    }

    [Fact]
    public void A_value_that_is_not_plain_text_is_refused_without_force_and_replaced_with_it()
    {
        var exe = MakeInstallFolder();
        _registry.Values[Name] = new RunKeyValue(Text: null); // for example a REG_DWORD or REG_EXPAND_SZ

        Assert.Equal(1, Manager().Install(exe, force: false, _output, _error));
        Assert.Empty(_registry.Calls);
        Assert.Equal(0, Manager().Install(exe, force: true, _output, _error));
        Assert.Equal(DaemonCommand(exe), _registry.Values[Name].Text);
    }

    [Fact]
    public void A_data_directory_other_than_the_default_is_handed_to_the_daemon()
    {
        var exe = MakeInstallFolder();
        var custom = Path.Combine(_root.FullName, "elsewhere");

        Manager(dataDirectory: custom, defaultDirectory: _dataDirectory).Install(exe, force: false, _output, _error);

        Assert.Equal($"{DaemonCommand(exe)} --data-dir \"{custom}\"", _registry.Values[Name].Text);
    }

    [Fact]
    public void Install_with_a_different_data_directory_than_the_existing_value_is_refused_without_force()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        _registry.Calls.Clear();

        var code = Manager(dataDirectory: Path.Combine(_root.FullName, "elsewhere"), defaultDirectory: _dataDirectory)
            .Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Empty(_registry.Calls);
    }

    [Fact]
    public void Install_keeps_an_existing_log_untouched()
    {
        var exe = MakeInstallFolder();
        Directory.CreateDirectory(_dataDirectory);
        var log = Path.Combine(_dataDirectory, "vigil.log");
        File.WriteAllText(log, "old");

        Manager().Install(exe, force: false, _output, _error);

        Assert.Equal("old", File.ReadAllText(log));
    }

    [Fact]
    public void Uninstall_stops_the_daemon_deletes_the_value_and_keeps_the_data_directory()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        _registry.Calls.Clear();
        _daemon.Result = new DaemonStopResult(DaemonStopOutcome.Stopped);

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(1, _daemon.StopCalls);
        Assert.Equal([$"delete {Name}"], _registry.Calls);
        Assert.Empty(_registry.Values);
        Assert.True(Directory.Exists(_dataDirectory));
        Assert.Contains("Stopped the running", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Uninstalled", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_when_nothing_is_installed_succeeds_and_changes_nothing()
    {
        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Contains("Not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_twice_is_idempotent()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        Manager().Uninstall(purge: false, _output, _error);

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void Uninstall_keeps_the_value_when_the_daemon_cannot_be_stopped()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        _registry.Calls.Clear();
        _daemon.Result = new DaemonStopResult(DaemonStopOutcome.Failed, "still answering");

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("still answering", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_registry.Calls);
        Assert.True(_registry.Values.ContainsKey(Name));
    }

    [Fact]
    public void Uninstall_removes_the_value_when_no_daemon_is_running()
    {
        _registry.Values[Name] = new RunKeyValue("\"C:\\gone\\vigild.exe\"");

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_registry.Values);
        Assert.Contains("Uninstalled", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_deletes_only_the_default_data_directory()
    {
        var exe = MakeInstallFolder();
        Manager().Install(exe, force: false, _output, _error);
        File.WriteAllText(Path.Combine(_dataDirectory, "config.json"), "{}");

        var code = Manager().Uninstall(purge: true, _output, _error);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(_dataDirectory));
        Assert.True(File.Exists(exe)); // The program folder is not Vigil's data.
    }

    [Fact]
    public void Purge_with_nothing_to_purge_succeeds()
    {
        Assert.Equal(0, Manager().Uninstall(purge: true, _output, _error));
        Assert.Contains("Nothing to purge", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_refuses_a_data_directory_that_is_not_the_default()
    {
        var custom = Path.Combine(_root.FullName, "Documents");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "keep.txt"), "x");

        var code = Manager(dataDirectory: custom, defaultDirectory: _dataDirectory).Uninstall(purge: true, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(custom, "keep.txt")));
        Assert.Contains("not the default", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_refuses_a_default_directory_that_is_not_named_Vigil()
    {
        var odd = Path.Combine(_root.FullName, "Home");
        Directory.CreateDirectory(odd);

        var code = Manager(dataDirectory: odd, defaultDirectory: odd).Uninstall(purge: true, _output, _error);

        Assert.Equal(1, code);
        Assert.True(Directory.Exists(odd));
    }

    [Fact]
    public void Purge_refuses_a_symbolic_link()
    {
        var target = Path.Combine(_root.FullName, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        try
        {
            Directory.CreateSymbolicLink(_dataDirectory, target);
        }
        catch (UnauthorizedAccessException)
        {
            return; // Windows lets a standard user create symbolic links only in developer mode.
        }

        var code = Manager().Uninstall(purge: true, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }
}
