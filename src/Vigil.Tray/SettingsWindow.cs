using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vigil.Commands;
using Vigil.Tray;

namespace Vigil.Tray.Ui;

/// <summary>
/// The status of the daemon (what <c>vigil status</c> prints, refreshed by the tray's poll) and the settings of config.json.
/// The window judges nothing: a value is checked by the daemon's own loader when it is saved (<see cref="SettingsStore"/>), and
/// the messages shown are the loader's. It is built in code, without XAML or bindings, so that nothing in it needs reflection.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly FontFamily Monospace = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly ISettingsStore _store;
    private readonly SettingsController _controller;

    private readonly SelectableTextBlock _status = new() { FontFamily = Monospace, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _threshold = new() { Width = 140 };
    private readonly CheckBox _pauseOnLock = new() { Content = "Pause while the screen is locked" };
    private readonly CheckBox _pauseOnBattery = new() { Content = "Pause while running on battery" };
    private readonly Dictionary<DayOfWeek, CheckBox> _days = SettingsForm.AllDays.ToDictionary(day => day, day => new CheckBox { Content = day.ToString()[..3] });
    private readonly TextBox _start = new() { Width = 90 };
    private readonly TextBox _end = new() { Width = 90 };
    private readonly CheckBox _activityLog = new() { Content = "Keep the daily activity log (counters only)" };
    private readonly TextBox _retention = new() { Width = 90 };
    private readonly ComboBox _strategy = new() { ItemsSource = SettingsForm.Strategies, Width = 140 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save" };

    public SettingsWindow(ISettingsStore store, SettingsController controller)
    {
        _store = store;
        _controller = controller;

        Title = "Vigil";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) => Populate();
        _save.Click += async (_, _) => await SaveAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        ToolTip.SetTip(_threshold, "Idle time at which Vigil acts, as hh:mm:ss (config key Vigil.Threshold).");
        ToolTip.SetTip(_start, "Start of the daily window, HH:mm. Equal start and end mean the whole day.");
        ToolTip.SetTip(_end, "End of the daily window, HH:mm. An end before the start crosses midnight.");
        ToolTip.SetTip(_retention, "Daily files older than this many days are deleted at the daemon's next start or at midnight.");
        ToolTip.SetTip(_strategy, "Which invisible event is posted on this operating system.");

        var dayRow = new WrapPanel();
        foreach (var box in _days.Values)
        {
            box.Margin = new Thickness(0, 0, 8, 0);
            dayRow.Children.Add(box);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(revert);
        buttons.Children.Add(_save);
        buttons.Children.Add(close);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), RowSpacing = 8 };
        AddRow(form, "Idle threshold (hh:mm:ss)", _threshold);
        AddRow(form, string.Empty, _pauseOnLock);
        AddRow(form, string.Empty, _pauseOnBattery);
        AddRow(form, "Active days", dayRow);
        AddRow(form, "Active from (HH:mm)", _start);
        AddRow(form, "Active until (HH:mm)", _end);
        AddRow(form, string.Empty, _activityLog);
        AddRow(form, "Keep activity files (days)", _retention);
        AddRow(form, "Input event", _strategy);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "Status", FontWeight = FontWeight.SemiBold });
        root.Children.Add(_status);
        root.Children.Add(new Separator());
        root.Children.Add(new TextBlock { Text = "Settings (config.json)", FontWeight = FontWeight.SemiBold });
        root.Children.Add(form);
        root.Children.Add(_message);
        root.Children.Add(buttons);
        Content = root;

        Populate();
    }

    /// <summary>The status block: the lines of <c>vigil status</c>, or why there is no daemon to ask.</summary>
    public void ShowStatus(DaemonReply reply)
    {
        if (reply is { Ok: true, Status: { } status })
        {
            using var text = new StringWriter();
            StatusFormatter.Write(text, status, reply.Platform);
            _status.Text = text.ToString().TrimEnd();
        }
        else
        {
            _status.Text = $"The Vigil daemon is not running.{Environment.NewLine}{reply.Error}{Environment.NewLine}" +
                $"The tray does not start it. Run 'vigil run' or install autostart with 'vigil install'.";
        }
    }

    private static void AddRow(Grid grid, string label, Control field)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        field.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(caption);
        grid.Children.Add(field);
    }

    /// <summary>Fills the fields from config.json as it is now, and shows what is wrong with it, if anything.</summary>
    private void Populate()
    {
        var load = _store.Load();
        Fill(load.Form);
        ShowMessages(load.Problems, isError: load.Problems.Count > 0);
    }

    private void Fill(SettingsForm form)
    {
        _threshold.Text = form.Threshold;
        _pauseOnLock.IsChecked = form.PauseOnLock;
        _pauseOnBattery.IsChecked = form.PauseOnBattery;
        foreach (var (day, box) in _days)
        {
            box.IsChecked = form.ActiveDays.Contains(day);
        }

        _start.Text = form.ScheduleStart;
        _end.Text = form.ScheduleEnd;
        _activityLog.IsChecked = form.ActivityLogEnabled;
        _retention.Text = form.RetentionDays;
        _strategy.SelectedItem = form.Strategy;
    }

    private SettingsForm Read() => new(
        _threshold.Text?.Trim() ?? string.Empty,
        _pauseOnLock.IsChecked == true,
        _pauseOnBattery.IsChecked == true,
        [.. SettingsForm.AllDays.Where(day => _days[day].IsChecked == true)],
        _start.Text?.Trim() ?? string.Empty,
        _end.Text?.Trim() ?? string.Empty,
        _activityLog.IsChecked == true,
        _retention.Text?.Trim() ?? string.Empty,
        _strategy.SelectedItem as string ?? SettingsForm.Strategies[0]);

    private async Task SaveAsync()
    {
        _save.IsEnabled = false;
        try
        {
            var outcome = await _controller.SaveAsync(Read(), CancellationToken.None);
            if (outcome.Saved)
            {
                // Show the file as it was written (the canonical notation), then the result.
                Fill(_store.Load().Form);
            }

            ShowMessages(outcome.Messages, isError: outcome.Kind is SaveOutcomeKind.Invalid or SaveOutcomeKind.SavedDaemonRefused);
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }

    private void ShowMessages(IReadOnlyList<string> messages, bool isError)
    {
        _message.Text = string.Join(Environment.NewLine, messages);
        _message.Foreground = isError ? Brushes.IndianRed : null;
        _message.IsVisible = messages.Count > 0;
    }
}
