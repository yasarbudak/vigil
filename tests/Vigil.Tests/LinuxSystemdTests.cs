using Vigil.Install;
using Vigil.Platform.Linux.Autostart;

namespace Vigil.Tests;

public class LinuxSystemdUnitTests
{
    private const string Binary = "/home/me/Apps/vigil";

    [Fact]
    public void The_unit_has_the_documented_shape_and_the_restart_policy_of_the_macOS_agent()
    {
        var unit = SystemdUserUnit.Render(new SystemdUnitSpec(Binary));

        Assert.Equal(
            "# Written by 'vigil install'. Remove it with 'vigil uninstall'.\n"
            + "[Unit]\n"
            + "Description=Vigil keeps chat apps from showing you as Away while you are at your desk\n"
            + "Documentation=https://github.com/yasarbudak/vigil\n"
            + "PartOf=graphical-session.target\n"
            + "After=graphical-session.target\n"
            + "\n"
            + "[Service]\n"
            + "Type=simple\n"
            + "ExecStart=\"/home/me/Apps/vigil\" run\n"
            + "Restart=on-abnormal\n"
            + "RestartSec=30\n"
            + "\n"
            + "[Install]\n"
            + "WantedBy=graphical-session.target\n",
            unit);
    }

    [Fact]
    public void A_deliberate_exit_is_not_restarted_because_the_policy_is_on_abnormal_not_on_failure()
    {
        var unit = SystemdUserUnit.Render(new SystemdUnitSpec(Binary));

        Assert.Contains("\nRestart=on-abnormal\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("on-failure", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=always", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_data_directory_is_passed_on_as_an_environment_entry()
    {
        var unit = SystemdUserUnit.Render(new SystemdUnitSpec(Binary, "/data/vigil dir"));

        Assert.Contains("\nEnvironment=\"VIGIL_DATA_DIR=/data/vigil dir\"\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment=", SystemdUserUnit.Render(new SystemdUnitSpec(Binary)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/my apps/vigil", "\"/opt/my apps/vigil\"")]
    [InlineData("/opt/100%/vigil", "\"/opt/100%%/vigil\"")] // % starts a specifier
    [InlineData("/opt/$HOME/vigil", "\"/opt/$$HOME/vigil\"")] // $ starts a variable in a command line
    [InlineData("/opt/a\"b/vigil", "\"/opt/a\\\"b/vigil\"")]
    [InlineData("/opt/a\\b/vigil", "\"/opt/a\\\\b/vigil\"")]
    public void The_command_is_quoted_so_that_systemd_reads_the_path_back_unchanged(string path, string expected)
    {
        Assert.Equal(expected, SystemdUserUnit.QuoteExecStart(path));
        Assert.Contains($"ExecStart={expected} run\n", SystemdUserUnit.Render(new SystemdUnitSpec(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void In_an_environment_entry_only_percent_is_doubled_because_dollar_has_no_meaning_there()
    {
        var unit = SystemdUserUnit.Render(new SystemdUnitSpec(Binary, "/d/100%/$x"));

        Assert.Contains("Environment=\"VIGIL_DATA_DIR=/d/100%%/$x\"\n", unit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/new\nline/vigil")]
    [InlineData("/opt/tab\t/vigil")]
    [InlineData("/opt/del\u007f/vigil")]
    public void Control_characters_are_refused_not_escaped(string path)
    {
        Assert.Throws<ArgumentException>(() => SystemdUserUnit.Render(new SystemdUnitSpec(path)));
    }

    [Fact]
    public void Relative_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SystemdUserUnit.Render(new SystemdUnitSpec("vigil")));
        Assert.Throws<ArgumentException>(() => SystemdUserUnit.Render(new SystemdUnitSpec(Binary, "relative")));
    }
}

/// <summary>The install and uninstall flow with a fake systemctl: the real systemd, ~/.config and the real data directory are never touched.</summary>
public sealed class LinuxSystemdUnitManagerTests : IDisposable
{
    private sealed class FakeSystemctl : ISystemctl
    {
        public bool ManagerAvailable { get; set; } = true;

        public bool Enabled { get; set; }

        public bool Active { get; set; }

        public SystemctlResult EnableResult { get; set; } = new(0, string.Empty);

        public SystemctlResult DisableResult { get; set; } = new(0, string.Empty);

        public SystemctlResult ReloadResult { get; set; } = new(0, string.Empty);

        public List<string> Calls { get; } = [];

        public SystemctlResult CheckUserManager()
        {
            Calls.Add("check");
            return ManagerAvailable ? new SystemctlResult(0, string.Empty) : new SystemctlResult(1, "Failed to connect to bus: No medium found");
        }

        public SystemctlResult DaemonReload()
        {
            Calls.Add("daemon-reload");
            return ReloadResult;
        }

        public SystemctlResult EnableNow(string unit)
        {
            Calls.Add($"enable --now {unit}");
            if (EnableResult.Succeeded)
            {
                Enabled = Active = true;
            }

            return EnableResult;
        }

        public SystemctlResult DisableNow(string unit)
        {
            Calls.Add($"disable --now {unit}");
            if (DisableResult.Succeeded)
            {
                Enabled = Active = false;
            }

            return DisableResult;
        }

        public bool IsEnabled(string unit) => Enabled;

        public bool IsActive(string unit) => Active;

        public IEnumerable<string> Changes => Calls.Where(c => c is not "check");
    }

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vg");
    private readonly FakeSystemctl _systemctl = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _units;
    private readonly string _dataDirectory;

    public LinuxSystemdUnitManagerTests()
    {
        _units = Path.Combine(_root.FullName, "systemd", "user");
        _dataDirectory = Path.Combine(_root.FullName, "vigil");
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _output.Dispose();
        _error.Dispose();
    }

    private string MakeBinary(string name = "bin")
    {
        var folder = Path.Combine(_root.FullName, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "vigil");
        File.WriteAllText(path, "x");
        return path;
    }

    private SystemdUnitManager Manager(string? dataDirectory = null, string? defaultDirectory = null) =>
        new(_systemctl, _units, new VigilPaths(dataDirectory ?? _dataDirectory), defaultDirectory ?? _dataDirectory);

    private string UnitPath => Path.Combine(_units, "vigil.service");

    [UnixOnlyFact]
    public void A_fresh_install_writes_the_unit_reloads_and_enables_it_now()
    {
        var binary = MakeBinary();

        var code = Manager().Install(binary, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(SystemdUserUnit.Render(new SystemdUnitSpec(binary)), File.ReadAllText(UnitPath));
        Assert.Equal(["daemon-reload", "enable --now vigil.service"], _systemctl.Changes);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(UnitPath)); // 0644
        }

        Assert.True(Directory.Exists(_dataDirectory));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "vigil.log"))); // the journal keeps the log
        Assert.Contains("journalctl --user -u vigil", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_error.ToString());
    }

    [UnixOnlyFact]
    public void Without_a_systemd_user_instance_install_refuses_and_writes_nothing()
    {
        _systemctl.ManagerAvailable = false;

        var code = Manager().Install(MakeBinary(), force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.False(File.Exists(UnitPath));
        Assert.False(Directory.Exists(_units));
        Assert.Empty(_systemctl.Changes);
        Assert.Contains("no systemd user instance", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("No medium found", _error.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void Installing_again_with_the_same_settings_changes_nothing()
    {
        var binary = MakeBinary();
        Manager().Install(binary, force: false, _output, _error);
        _systemctl.Calls.Clear();
        var before = File.ReadAllBytes(UnitPath);

        var code = Manager().Install(binary, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(before, File.ReadAllBytes(UnitPath));
        Assert.Empty(_systemctl.Changes);
        Assert.Contains("Already installed and running", _output.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void An_identical_unit_that_is_not_running_is_only_brought_up()
    {
        var binary = MakeBinary();
        Manager().Install(binary, force: false, _output, _error);
        _systemctl.Enabled = _systemctl.Active = false;
        _systemctl.Calls.Clear();

        var code = Manager().Install(binary, force: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(["daemon-reload", "enable --now vigil.service"], _systemctl.Changes);
    }

    [UnixOnlyFact]
    public void A_different_unit_is_refused_without_force_and_left_alone()
    {
        var first = MakeBinary("one");
        Manager().Install(first, force: false, _output, _error);
        _systemctl.Calls.Clear();
        var before = File.ReadAllText(UnitPath);

        var code = Manager().Install(MakeBinary("two"), force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.Equal(before, File.ReadAllText(UnitPath));
        Assert.Empty(_systemctl.Changes);
        Assert.Contains("--force", _error.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void Force_stops_the_running_unit_then_writes_the_new_one_and_starts_it()
    {
        Manager().Install(MakeBinary("one"), force: false, _output, _error);
        _systemctl.Calls.Clear();
        var second = MakeBinary("two");

        var code = Manager().Install(second, force: true, _output, _error);

        Assert.Equal(0, code);
        Assert.Equal(["disable --now vigil.service", "daemon-reload", "enable --now vigil.service"], _systemctl.Changes);
        Assert.Equal(SystemdUserUnit.Render(new SystemdUnitSpec(second)), File.ReadAllText(UnitPath));
    }

    [UnixOnlyFact]
    public void If_the_old_unit_cannot_be_stopped_the_old_file_stays()
    {
        Manager().Install(MakeBinary("one"), force: false, _output, _error);
        var before = File.ReadAllText(UnitPath);
        _systemctl.DisableResult = new SystemctlResult(1, "Job failed");

        var code = Manager().Install(MakeBinary("two"), force: true, _output, _error);

        Assert.Equal(1, code);
        Assert.Equal(before, File.ReadAllText(UnitPath));
        Assert.Contains("disable --now failed", _error.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void A_failed_start_leaves_the_unit_written_and_says_where_to_look()
    {
        _systemctl.EnableResult = new SystemctlResult(1, "Unit is masked");

        var code = Manager().Install(MakeBinary(), force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(UnitPath));
        Assert.Contains("Unit is masked", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("journalctl --user -u vigil.service", _error.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void A_failed_reload_stops_before_enabling()
    {
        _systemctl.ReloadResult = new SystemctlResult(1, "Access denied");

        var code = Manager().Install(MakeBinary(), force: false, _output, _error);

        Assert.Equal(1, code);
        Assert.DoesNotContain("enable --now vigil.service", _systemctl.Calls);
    }

    [UnixOnlyFact]
    public void A_custom_data_directory_is_written_into_the_unit()
    {
        var custom = Path.Combine(_root.FullName, "elsewhere");

        Manager(dataDirectory: custom, defaultDirectory: _dataDirectory).Install(MakeBinary(), force: false, _output, _error);

        Assert.Contains($"Environment=\"VIGIL_DATA_DIR={custom}\"", File.ReadAllText(UnitPath), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void Install_refuses_a_binary_that_is_not_an_absolute_existing_path()
    {
        Assert.Equal(1, Manager().Install(null, force: false, _output, _error));
        Assert.Equal(1, Manager().Install("vigil", force: false, _output, _error));
        Assert.Equal(1, Manager().Install(Path.Combine(_root.FullName, "missing", "vigil"), force: false, _output, _error));
        Assert.Empty(_systemctl.Calls);
    }

    [UnixOnlyFact]
    public void Uninstall_stops_and_disables_the_unit_then_removes_the_file()
    {
        Manager().Install(MakeBinary(), force: false, _output, _error);
        _systemctl.Calls.Clear();

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.False(File.Exists(UnitPath));
        Assert.Equal(["disable --now vigil.service", "daemon-reload"], _systemctl.Changes);
        Assert.Contains("Uninstalled", _output.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(_dataDirectory)); // kept without --purge
    }

    [UnixOnlyFact]
    public void Uninstall_keeps_the_file_when_the_unit_cannot_be_stopped()
    {
        Manager().Install(MakeBinary(), force: false, _output, _error);
        _systemctl.DisableResult = new SystemctlResult(1, "Job failed");

        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(1, code);
        Assert.True(File.Exists(UnitPath));
    }

    [UnixOnlyFact]
    public void Uninstall_with_nothing_installed_says_so_and_changes_nothing()
    {
        var code = Manager().Uninstall(purge: false, _output, _error);

        Assert.Equal(0, code);
        Assert.Empty(_systemctl.Changes);
        Assert.Contains("Not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void Purge_deletes_only_the_default_data_directory()
    {
        Manager().Install(MakeBinary(), force: false, _output, _error);
        File.WriteAllText(Path.Combine(_dataDirectory, "config.json"), "{}");

        Assert.Equal(0, Manager().Uninstall(purge: true, _output, _error));

        Assert.False(Directory.Exists(_dataDirectory));
        Assert.True(Directory.Exists(_root.FullName));
    }

    [UnixOnlyFact]
    public void Purge_refuses_a_data_directory_that_is_not_the_default()
    {
        var custom = Path.Combine(_root.FullName, "elsewhere");
        Directory.CreateDirectory(custom);

        var code = Manager(dataDirectory: custom, defaultDirectory: _dataDirectory).Uninstall(purge: true, _output, _error);

        Assert.Equal(1, code);
        Assert.True(Directory.Exists(custom));
    }

    [UnixOnlyFact]
    public void Purge_accepts_the_lower_case_default_name_of_Linux_and_still_refuses_other_names()
    {
        // The test directory itself is named "vigil" (lower case, as XDG says); a directory with another name is never purged.
        var other = Path.Combine(_root.FullName, "something-else");
        Directory.CreateDirectory(other);

        Assert.Equal(1, Manager(dataDirectory: other, defaultDirectory: other).Uninstall(purge: true, _output, _error));
        Assert.True(Directory.Exists(other));

        Directory.CreateDirectory(_dataDirectory);
        Assert.Equal(0, Manager().Uninstall(purge: true, _output, _error));
        Assert.False(Directory.Exists(_dataDirectory));
    }
}
