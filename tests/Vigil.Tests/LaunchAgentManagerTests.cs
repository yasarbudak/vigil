using System.Runtime.Versioning;
using Vigil.Install;
using Vigil.Platform.MacOS.LaunchAgent;

namespace Vigil.Tests;

/// <summary>Checks Unix permission bits and symbolic links; the install feature itself is macOS only.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class LaunchAgentManagerTests : IDisposable
{
    private const string Label = "io.github.yasarbudak.vigil";

    private sealed class FakeLaunchctl : ILaunchctl
    {
        public bool Loaded { get; set; }

        public LaunchctlResult BootstrapResult { get; set; } = new(0, string.Empty);

        public LaunchctlResult BootoutResult { get; set; } = new(0, string.Empty);

        public List<string> Calls { get; } = [];

        public bool IsLoaded(string label)
        {
            Calls.Add($"print {label}");
            return Loaded;
        }

        public LaunchctlResult Bootstrap(string plistPath)
        {
            Calls.Add($"bootstrap {plistPath}");
            Loaded = Loaded || BootstrapResult.Succeeded;
            return BootstrapResult;
        }

        public LaunchctlResult Bootout(string label)
        {
            Calls.Add($"bootout {label}");
            Loaded = Loaded && !BootoutResult.Succeeded;
            return BootoutResult;
        }

        public IEnumerable<string> Changes => Calls.Where(c => !c.StartsWith("print", StringComparison.Ordinal));
    }

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");
    private readonly FakeLaunchctl _launchctl = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _agents;
    private readonly string _dataDirectory;

    public LaunchAgentManagerTests()
    {
        _agents = Path.Combine(_root.FullName, "LaunchAgents");
        _dataDirectory = Path.Combine(_root.FullName, "Vigil");
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _output.Dispose();
        _error.Dispose();
    }

    private string MakeBundle(string name = "Vigil.app", bool withInfoPlist = true)
    {
        var macOs = Path.Combine(_root.FullName, name, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        if (withInfoPlist)
        {
            File.WriteAllText(Path.Combine(_root.FullName, name, "Contents", "Info.plist"), "<plist/>");
        }

        return Path.Combine(macOs, "vigil");
    }

    private LaunchAgentManager Manager(string? dataDirectory = null, string? defaultDirectory = null) =>
        new(_launchctl, _agents, new VigilPaths(dataDirectory ?? _dataDirectory), defaultDirectory ?? _dataDirectory);

    private string PlistPath => Path.Combine(_agents, Label + ".plist");

    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);

    [Fact]
    public void Install_outside_an_app_bundle_is_refused_and_changes_nothing()
    {
        var exe = Path.Combine(_root.FullName, "bin", "vigil");

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("not inside an app bundle", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("permission", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_launchctl.Calls);
        Assert.False(Directory.Exists(_agents));
        Assert.False(Directory.Exists(_dataDirectory));
    }

    [Fact]
    public void Install_without_a_current_executable_path_is_refused()
    {
        Assert.Equal(1, Manager().Install(null, force: false, _output, _error));
        Assert.Empty(_launchctl.Calls);
    }

    [Fact]
    public void Install_from_a_bundle_without_Info_plist_is_refused()
    {
        var exe = MakeBundle(withInfoPlist: false);

        Assert.Equal(1, Manager().Install(exe, force: false, _output, _error));
        Assert.Contains("Info.plist", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_launchctl.Calls);
    }

    [Fact]
    public void Fresh_install_writes_the_plist_prepares_the_log_and_bootstraps_once()
    {
        var exe = MakeBundle();

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal([$"bootstrap {PlistPath}"], _launchctl.Changes);
        var plist = File.ReadAllText(PlistPath);
        Assert.Contains($"<string>{exe}</string>\n\t\t<string>run</string>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardOutPath", plist, StringComparison.Ordinal);
        Assert.Contains($"Log: {Path.Combine(_dataDirectory, "vigil.log")}", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, Mode(PlistPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode(Path.Combine(_dataDirectory, "vigil.log")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, Mode(_dataDirectory));
        Assert.Empty(Directory.GetFiles(_agents, ".*"));
    }

    [Fact]
    public void Install_keeps_the_log_private_even_if_it_already_exists_with_looser_permissions()
    {
        var exe = MakeBundle();
        Directory.CreateDirectory(_dataDirectory);
        var log = Path.Combine(_dataDirectory, "vigil.log");
        File.WriteAllText(log, "old");
        File.SetUnixFileMode(log, (UnixFileMode)0b110_100_100);

        Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode(log));
        Assert.Equal("old", File.ReadAllText(log));
    }

    [Fact]
    public void Install_again_with_the_same_executable_is_a_no_op()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        _launchctl.Calls.Clear();
        var before = File.GetLastWriteTimeUtc(PlistPath);

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_launchctl.Changes);
        Assert.Equal(before, File.GetLastWriteTimeUtc(PlistPath));
        Assert.Contains("Already installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_with_an_unchanged_plist_that_is_not_loaded_only_bootstraps()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        _launchctl.Loaded = false;
        _launchctl.Calls.Clear();

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal([$"bootstrap {PlistPath}"], _launchctl.Changes);
    }

    [Fact]
    public void Install_pointing_elsewhere_is_refused_without_force_and_leaves_the_plist_alone()
    {
        var first = MakeBundle("One.app");
        var second = MakeBundle("Two.app");
        Manager().Install(first, force: false, _output, _error);
        var original = File.ReadAllText(PlistPath);
        _launchctl.Calls.Clear();

        var code = Manager().Install(second, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("--force", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains(PlistPath, _error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first, _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(PlistPath));
        Assert.Empty(_launchctl.Changes);
    }

    [Fact]
    public void Install_with_force_unloads_replaces_and_loads_again()
    {
        var first = MakeBundle("One.app");
        var second = MakeBundle("Two.app");
        Manager().Install(first, force: false, _output, _error);
        _launchctl.Calls.Clear();

        var code = Manager().Install(second, force: true, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal([$"bootout {Label}", $"bootstrap {PlistPath}"], _launchctl.Changes);
        Assert.Contains($"<string>{second}</string>", File.ReadAllText(PlistPath), StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_plist_that_cannot_be_read_is_not_overwritten_without_force()
    {
        var exe = MakeBundle();
        Directory.CreateDirectory(_agents);
        File.WriteAllText(PlistPath, "bplist00 binary or foreign");

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Equal("bplist00 binary or foreign", File.ReadAllText(PlistPath));
        Assert.Empty(_launchctl.Changes);
    }

    [Fact]
    public void Install_with_a_different_data_directory_than_the_existing_plist_is_refused_without_force()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        var original = File.ReadAllBytes(PlistPath);
        _launchctl.Calls.Clear();

        var code = Manager(dataDirectory: Path.Combine(_root.FullName, "elsewhere"), defaultDirectory: _dataDirectory)
            .Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Equal(original, File.ReadAllBytes(PlistPath));
        Assert.Empty(_launchctl.Changes);
    }

    [Fact]
    public void Install_with_force_over_a_different_data_directory_rewrites_the_plist_with_the_variable()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);

        var code = Manager(dataDirectory: Path.Combine(_root.FullName, "elsewhere"), defaultDirectory: _dataDirectory)
            .Install(exe, force: true, _output, _error);

        Assert.Equal(0, code);
        Assert.Contains("VIGIL_DATA_DIR", File.ReadAllText(PlistPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_plist_is_refused_without_force_and_replaced_with_it()
    {
        var exe = MakeBundle();
        Directory.CreateDirectory(_agents);
        File.WriteAllBytes(PlistPath, [0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x30, 0x30, 0xFF, 0xFE, 0x00]);

        Assert.Equal(1, Manager().Install(exe, force: false, _output, _error));
        Assert.Equal(0, Manager().Install(exe, force: true, _output, _error));
        Assert.Contains("<key>Label</key>", File.ReadAllText(PlistPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bootstrap_failure_is_reported_with_the_exit_code_and_output()
    {
        var exe = MakeBundle();
        _launchctl.BootstrapResult = new LaunchctlResult(5, "Bootstrap failed: 5: Input/output error");

        var code = Manager().Install(exe, force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Contains("exit 5", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Input/output error", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bootout_failure_during_a_forced_replace_stops_before_the_plist_changes()
    {
        var first = MakeBundle("One.app");
        var second = MakeBundle("Two.app");
        Manager().Install(first, force: false, _output, _error);
        var original = File.ReadAllText(PlistPath);
        _launchctl.BootoutResult = new LaunchctlResult(1, "nope");

        var code = Manager().Install(second, force: true, _output, _error);

        Assert.Equal(1, code);
        Assert.Equal(original, File.ReadAllText(PlistPath));
    }

    [Fact]
    public void A_data_directory_other_than_the_default_is_handed_to_the_daemon()
    {
        var exe = MakeBundle();
        var custom = Path.Combine(_root.FullName, "elsewhere");

        Manager(dataDirectory: custom, defaultDirectory: _dataDirectory).Install(exe, force: false, _output, _error);

        var plist = File.ReadAllText(PlistPath);
        Assert.Contains("VIGIL_DATA_DIR", plist, StringComparison.Ordinal);
        Assert.Contains(custom, plist, StringComparison.Ordinal);
    }

    [Fact]
    public void Atomic_write_replaces_the_content_and_leaves_no_temporary_file()
    {
        Directory.CreateDirectory(_agents);
        var path = Path.Combine(_agents, "x.plist");
        File.WriteAllText(path, "old");

        LaunchAgentManager.WriteAtomically(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(_agents));
    }

    [Fact]
    public void Uninstall_unloads_deletes_the_plist_and_keeps_the_data_directory()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        _launchctl.Calls.Clear();

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal([$"bootout {Label}"], _launchctl.Changes);
        Assert.False(File.Exists(PlistPath));
        Assert.True(Directory.Exists(_dataDirectory));
    }

    [Fact]
    public void Uninstall_when_nothing_is_installed_succeeds_and_changes_nothing()
    {
        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_launchctl.Changes);
        Assert.Contains("Not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_twice_is_idempotent()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        Manager().Uninstall(purge: false, _output, _error);

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(1, _launchctl.Calls.Count(c => c.StartsWith("bootout", StringComparison.Ordinal)));
    }

    [Fact]
    public void Uninstall_keeps_the_plist_when_the_job_cannot_be_unloaded()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        _launchctl.BootoutResult = new LaunchctlResult(1, "nope");

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(PlistPath));
    }

    [Fact]
    public void Uninstall_removes_a_plist_that_is_no_longer_loaded()
    {
        Directory.CreateDirectory(_agents);
        File.WriteAllText(PlistPath, "x");

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.False(File.Exists(PlistPath));
        Assert.Empty(_launchctl.Changes);
    }

    [Fact]
    public void Purge_deletes_only_the_default_data_directory_and_not_the_agents_directory()
    {
        var exe = MakeBundle();
        Manager().Install(exe, force: false, _output, _error);
        File.WriteAllText(Path.Combine(_dataDirectory, "config.json"), "{}");

        var code = Manager().Uninstall(purge: true, _output, _error);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(_dataDirectory));
        Assert.True(Directory.Exists(_agents));
        Assert.True(Directory.Exists(Path.GetDirectoryName(exe)));
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
        Directory.CreateSymbolicLink(_dataDirectory, target);

        var code = Manager().Uninstall(purge: true, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }
}
