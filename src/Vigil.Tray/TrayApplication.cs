using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Vigil.Tray;

namespace Vigil.Tray.Ui;

/// <summary>
/// Owns the tray icon, its menu, the settings window and the poll that keeps them current. All state of the daemon is read
/// through <see cref="IDaemonClient"/> and turned into what is shown by <see cref="TrayModel"/>; this class only places it.
/// </summary>
internal sealed class TrayApplication(VigilPaths paths, string platformSection) : Application
{
    // The tray polls a local socket, which costs next to nothing. While the window is open it follows the daemon more closely.
    private static readonly TimeSpan PollWhileClosed = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollWhileOpen = TimeSpan.FromSeconds(2);

    private readonly DaemonClient _daemon = DaemonClient.ForSocket(paths.SocketPath);
    private readonly Dictionary<TrayIconKind, WindowIcon> _icons = [];
    private readonly DispatcherTimer _poll = new() { Interval = PollWhileClosed };

    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private NativeMenuItem? _stateItem;
    private NativeMenuItem? _todayItem;
    private NativeMenuItem? _resumeItem;
    private readonly Dictionary<PauseChoice, NativeMenuItem> _pauseItems = [];
    private SettingsWindow? _window;
    private DaemonReply _latest = DaemonReply.Unreachable("not asked yet");
    private bool _refreshing;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        _lifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        foreach (var kind in Enum.GetValues<TrayIconKind>())
        {
            _icons[kind] = TrayIconRenderer.Render(kind);
        }

        // Avalonia owns the icon from here on: it removes it from the tray when the application shuts down.
        var trayIcon = new TrayIcon
        {
            Icon = _icons[TrayIconKind.NotRunning],
            ToolTipText = "Vigil",
            Menu = BuildMenu(),
        };
        MacOSProperties.SetIsTemplateIcon(trayIcon, TrayIconRenderer.UsesTemplateImages);

        // Windows raises Clicked for a left click (the right click opens the menu); macOS never raises it (TrayIcon.Clicked).
        trayIcon.Clicked += (_, _) => OpenWindow();
        TrayIcon.SetIcons(this, [trayIcon]);

        _poll.Tick += async (_, _) => await RefreshAsync();
        _poll.Start();
        _ = RefreshAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        _stateItem = new NativeMenuItem { Header = TrayModel.NotRunning().StateLine, IsEnabled = false };
        _todayItem = new NativeMenuItem { Header = TrayModel.NotRunning().TodayLine, IsEnabled = false };
        menu.Items.Add(_stateItem);
        menu.Items.Add(_todayItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        foreach (var choice in Enum.GetValues<PauseChoice>())
        {
            var item = new NativeMenuItem { Header = TrayCommands.Label(choice), IsEnabled = false };
            item.Click += async (_, _) => await SendAsync(TrayCommands.Pause(choice));
            _pauseItems[choice] = item;
            menu.Items.Add(item);
        }

        _resumeItem = new NativeMenuItem { Header = "Resume", IsEnabled = false };
        _resumeItem.Click += async (_, _) => await SendAsync(TrayCommands.Resume());
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var open = new NativeMenuItem { Header = "Open Vigil…" };
        open.Click += (_, _) => OpenWindow();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem
        {
            Header = "Quit tray",
            ToolTip = "Closes this tray icon only. The Vigil daemon keeps running.",
        };
        quit.Click += (_, _) => _lifetime?.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private async Task SendAsync(Vigil.Ipc.IpcRequest request)
    {
        Show(await _daemon.SendAsync(request, CancellationToken.None));
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            Show(await _daemon.SendAsync(TrayCommands.Status(), CancellationToken.None));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Puts a reply on the icon, the menu and the window (when it is open).</summary>
    private void Show(DaemonReply reply)
    {
        // A refusal (the daemon is there but said no) says nothing about the state: keep what is shown until the next status.
        if (reply is { Reachable: true, Ok: false })
        {
            return;
        }

        _latest = reply;
        var model = TrayModel.From(reply);

        if (TrayIcon.GetIcons(this) is [var trayIcon, ..])
        {
            trayIcon.Icon = _icons[model.Icon];
            trayIcon.ToolTipText = model.ToolTip;
        }

        _stateItem?.Header = model.StateLine;
        _todayItem?.Header = model.TodayLine;
        foreach (var item in _pauseItems.Values)
        {
            item.IsEnabled = model.CanPause;
        }

        _resumeItem?.IsEnabled = model.CanResume;
        _window?.ShowStatus(reply);
    }

    private void OpenWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var store = new SettingsStore(paths, platformSection);
        _window = new SettingsWindow(store, new SettingsController(store, _daemon));
        _window.Closed += (_, _) =>
        {
            _window = null;
            _poll.Interval = PollWhileClosed;
        };
        _poll.Interval = PollWhileOpen;
        _window.ShowStatus(_latest);
        _window.Show();
        _window.Activate();
    }
}
