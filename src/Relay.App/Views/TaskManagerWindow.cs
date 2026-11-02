using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Relay.App.Controls;
using Relay.Browser;
using Relay.Core.Performance;

namespace Relay.App.Views;

/// <summary>Shift+Esc: real memory and CPU for every tab and engine process, refreshed every two seconds.</summary>
public sealed class TaskManagerWindow : Window
{
    private readonly BrowserServices _s;
    private readonly ProcessMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ListBox _list = new();
    private readonly TextBlock _total = new();
    private readonly TextBlock _summary = new();
    private readonly Button _unload;
    private readonly Button _open;
    private bool _sampling;

    public TaskManagerWindow(BrowserServices services)
    {
        _s = services;
        Title = "RELAY Task manager";
        Width = 640;
        Height = 520;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(FontSizeProperty, "Font.Size");
        UseLayoutRounding = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
        if (App.Current.WindowManager?.LastActive is { IsVisible: true } owner)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
            Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);
        }

        var root = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "Task manager", Style = (Style)FindResource("Text.Heading"), FontSize = 16 });
        _total.Margin = new Thickness(0, 6, 0, 0);
        _total.FontSize = 22;
        _total.FontWeight = FontWeights.SemiBold;
        _total.Text = "Measuring…";
        header.Children.Add(_total);
        _summary.Style = (Style)FindResource("Text.Secondary");
        _summary.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _open = new Button { Content = "Go to tab", Style = (Style)FindResource("Button.Secondary"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        _open.Click += (_, _) => OpenSelected();
        _unload = new Button { Content = "Unload to free memory", Style = (Style)FindResource("Button.Primary"), IsEnabled = false };
        _unload.Click += (_, _) => UnloadSelected();
        buttons.Children.Add(_open);
        buttons.Children.Add(_unload);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var columns = Row("Process", "Memory", "CPU", null, isHeader: true);
        columns.Margin = new Thickness(10, 0, 10, 4);
        DockPanel.SetDock(columns, Dock.Top);
        root.Children.Add(columns);

        _list.Style = (Style)FindResource("PlainList");
        _list.ItemContainerStyle = (Style)FindResource("ListRow");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_list);

        Content = root;
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this, App.Current.Theme.IsDark);
        App.Current.Theme.ThemeChanged += OnThemeChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => NativeMethods.StyleWindow(this, App.Current.Theme.IsDark);

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        App.Current.Theme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    private static IReadOnlyList<BrowserTab> AllTabs() =>
        App.Current.WindowManager?.All.SelectMany(w => w.ViewModel.Tabs).ToList() ?? [];

    private async Task RefreshAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var tabs = AllTabs();
            var snap = await _monitor.SampleAsync(_s.Environment.Current, tabs);
            var selectedPid = (_list.SelectedItem as ListBoxItem)?.Tag is ProcessRow r ? r.ProcessId : (int?)null;

            _total.Text = $"{UsageFormat.Bytes(snap.TotalMemoryBytes)} total" +
                          (snap.TotalCpuPercent is { } c ? $" · {UsageFormat.Percent(c)}% CPU" : string.Empty);
            var loaded = tabs.Count - snap.UnloadedTabs;
            _summary.Text = $"{tabs.Count} {(tabs.Count == 1 ? "tab" : "tabs")}: {loaded} loaded, {snap.UnloadedTabs} unloaded (0 MB each). " +
                            $"Includes RELAY and all {snap.Rows.Count - 1} engine processes (msedgewebview2.exe), measured like Windows Task Manager.";

            _list.Items.Clear();
            foreach (var row in snap.Rows.OrderByDescending(r => r.IsTab).ThenByDescending(r => r.MemoryBytes))
            {
                var item = new ListBoxItem
                {
                    Tag = row,
                    Content = Row(row.Name, UsageFormat.Bytes(row.MemoryBytes), row.CpuPercent is { } p ? UsageFormat.Percent(p) + "%" : "–", row.Detail, false),
                };
                _list.Items.Add(item);
                if (row.ProcessId == selectedPid) _list.SelectedItem = item;
            }
            UpdateButtons();
        }
        catch (Exception ex) { _s.Paths.LogError("Task manager", ex); }
        finally { _sampling = false; }
    }

    private Grid Row(string name, string memory, string cpu, string? detail, bool isHeader)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

        var left = new StackPanel { Margin = new Thickness(0, isHeader ? 0 : 4, 8, isHeader ? 0 : 4) };
        left.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = isHeader ? FontWeights.Normal : FontWeights.SemiBold });
        if (detail is not null)
            left.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Text.Caption"), TextTrimming = TextTrimming.CharacterEllipsis });
        g.Children.Add(left);

        var mem = new TextBlock { Text = memory, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var c = new TextBlock { Text = cpu, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mem, 1);
        Grid.SetColumn(c, 2);
        g.Children.Add(mem);
        g.Children.Add(c);
        if (isHeader)
            foreach (var t in new[] { (TextBlock)left.Children[0], mem, c })
                t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return g;
    }

    private ProcessRow? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ProcessRow;

    private void UpdateButtons()
    {
        var row = Selected;
        _open.IsEnabled = row is { Tabs.Count: > 0 };
        _unload.IsEnabled = row is not null && row.Tabs.Any(t => t.CanDiscard);
    }

    private void OpenSelected()
    {
        if (Selected is not { Tabs.Count: > 0 } row) return;
        var tab = row.Tabs[0];
        var window = App.Current.WindowManager?.All.FirstOrDefault(w => w.ViewModel.Tabs.Contains(tab));
        if (window is null) return;
        window.ViewModel.ActiveTab = tab;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async void UnloadSelected()
    {
        if (Selected is not { } row) return;
        foreach (var t in row.Tabs) t.Discard();
        await Task.Delay(400);
        await RefreshAsync();
    }
}
