using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Relay.App.Controls;
using Relay.App.Themes;
using Relay.App.ViewModels;
using Relay.Browser;
using Relay.Core.Bookmarks;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.App.Views;

public partial class MainWindow : Window
{
    private readonly BrowserServices _s;
    private BrowserTab? _observedTab;
    private bool _settingAddress;
    private bool _editingAddress;
    private bool _closeReady;
    private Bookmark? _editingBookmark;
    private readonly DispatcherTimer _downloadPopupTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Fullscreen state
    private bool _isFullScreen;
    private WindowState _preFullState;
    private Rect _preFullBounds;

    public MainWindow(BrowserServices services, bool isPrivate)
    {
        _s = services;
        IsPrivate = isPrivate;
        ViewModel = new MainViewModel(services, isPrivate);
        DataContext = ViewModel;
        InitializeComponent();

        if (isPrivate)
        {
            ThemeManager.ApplyPrivatePalette(this);
            Title = "RELAY — Private";
        }

        RestoreBounds_();
        ApplyDensity();

        ViewModel.CloseWindowRequested += (_, _) => Dispatcher.BeginInvoke(Close);
        ViewModel.FocusAddressRequested += (_, _) => Dispatcher.BeginInvoke(FocusAddress, DispatcherPriority.Input);
        ViewModel.NewWindowRequested += (_, priv) => App.Current.WindowManager?.Open(priv);
        ViewModel.FullScreenRequested += (_, full) => SetFullScreen(full);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SplitChanged += (_, _) => ApplySplitLayout();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.VerticalTabsNarrow)) UpdateVerticalWidth();
            if (e.PropertyName == nameof(MainViewModel.VerticalTabs)) ApplyZenLayout();
        };
        Loaded += (_, _) => { UpdateVerticalWidth(); ApplyZenLayout(); };
        Toolbar.SizeChanged += (_, _) => ApplyDensity();

        StripHost.SizeChanged += (_, _) => TabsList.MaxWidth = Math.Max(0, StripHost.ActualWidth - 40 - 56);
        StateChanged += (_, _) => UpdateMaximizedLayout();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this, isPrivate || App.Current.Theme.IsDark);
        App.Current.Theme.ThemeChanged += OnThemeChanged;

        BookmarkPopup.CustomPopupPlacementCallback = AlignRight;
        DownloadPopup.CustomPopupPlacementCallback = AlignRight;
        ShieldPopup.CustomPopupPlacementCallback = AlignRight;
        _downloadPopupTimer.Tick += (_, _) => { _downloadPopupTimer.Stop(); if (!DownloadPopup.IsMouseOver) DownloadPopup.IsOpen = false; };

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { ViewModel.Omnibox.Close(); };
        LocationChanged += (_, _) => RepositionPopups();
    }

    public bool IsPrivate { get; }
    public MainViewModel ViewModel { get; }

    // ======================================================================================
    // Window chrome
    // ======================================================================================

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        NativeMethods.StyleWindow(this, IsPrivate || App.Current.Theme.IsDark);
        ApplyDensity();
        foreach (var t in ViewModel.Tabs) t.ApplySettings();
    }

    private void ApplyDensity()
    {
        var h = TryFindResource("Strip.Height") is double d ? d : 44;
        // Vertical tabs: the tab strip is hidden and the toolbar becomes the title bar.
        if (ViewModel.VerticalTabs) h = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 48;
        Chrome.CaptionHeight = _isFullScreen ? 0 : h;
    }

    private void UpdateMaximizedLayout()
    {
        if (WindowState == WindowState.Maximized && !_isFullScreen)
        {
            // A maximized custom-chrome window overhangs the screen by the resize border.
            RootBorder.Padding = SystemParameters.WindowResizeBorderThickness;
            MaxButton.Content = MaxButton2.Content = Icons.Restore;
            MaxButton.ToolTip = MaxButton2.ToolTip = "Restore";
        }
        else
        {
            RootBorder.Padding = new Thickness(0);
            MaxButton.Content = MaxButton2.Content = Icons.Maximize;
            MaxButton.ToolTip = MaxButton2.ToolTip = "Maximize";
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void RestoreBounds_()
    {
        var s = _s.Settings.Current;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        var others = App.Current.WindowManager?.All.Where(w => !ReferenceEquals(w, this) && w.IsLoaded).ToList() ?? [];
        if (others.Count > 0)
        {
            var last = others[^1];
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (last.WindowState == WindowState.Normal ? last.Left : 40) + 28;
            Top = (last.WindowState == WindowState.Normal ? last.Top : 40) + 28;
        }
        else if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
                 s.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 && s.WindowTop >= SystemParameters.VirtualScreenTop - 10 &&
                 s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                 s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
            if (s.WindowMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SaveBounds()
    {
        if (IsPrivate || _isFullScreen) return;
        var r = RestoreBounds;
        if (r.IsEmpty || r.Width < 100) return;
        try
        {
            _s.Settings.Current.WindowLeft = r.Left;
            _s.Settings.Current.WindowTop = r.Top;
            _s.Settings.Current.WindowWidth = r.Width;
            _s.Settings.Current.WindowHeight = r.Height;
            _s.Settings.Current.WindowMaximized = WindowState == WindowState.Maximized;
            _s.Settings.Save();
        }
        catch (Exception ex) { _s.Paths.LogError("Saving window bounds", ex); }
    }

    private CustomPopupPlacement[] AlignRight(Size popupSize, Size targetSize, Point offset) =>
        [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 12, targetSize.Height + 2), PopupPrimaryAxis.Horizontal)];

    private void RepositionPopups()
    {
        foreach (var p in new[] { BookmarkPopup, DownloadPopup, SiteInfoPopup, SuggestionsPopup, ShieldPopup, ToastPopup, QrPopup })
        {
            if (!p.IsOpen) continue;
            var o = p.HorizontalOffset;
            p.HorizontalOffset = o + 0.01;
            p.HorizontalOffset = o;
        }
    }

    // ======================================================================================
    // Fullscreen (page-requested or F11)
    // ======================================================================================

    public void SetFullScreen(bool full)
    {
        if (full == _isFullScreen) return;
        _isFullScreen = full;
        ViewModel.IsFullScreen = full;
        var chromeVisibility = full ? Visibility.Collapsed : Visibility.Visible;
        StripRow.Visibility = chromeVisibility;
        Toolbar.Visibility = chromeVisibility;
        Rail.Visibility = full ? Visibility.Collapsed : (ViewModel.SidebarEnabled ? Visibility.Visible : Visibility.Collapsed);
        SidePanel.Visibility = full || !ViewModel.Sidebar.IsOpen ? Visibility.Collapsed : Visibility.Visible;
        ApplyZenLayout();

        if (full)
        {
            _preFullState = WindowState;
            _preFullBounds = RestoreBounds;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            if (NativeMethods.MonitorBounds(this) is Rect m)
            {
                Left = m.Left; Top = m.Top; Width = m.Width; Height = m.Height;
            }
            RootBorder.Padding = new Thickness(0);
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            if (!_preFullBounds.IsEmpty)
            {
                Left = _preFullBounds.Left; Top = _preFullBounds.Top; Width = _preFullBounds.Width; Height = _preFullBounds.Height;
            }
            WindowState = _preFullState;
            UpdateMaximizedLayout();
            // Rebind sidebar visibility (we set local values above).
            Rail.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(MainViewModel.SidebarEnabled)) { Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVis") });
            SidePanel.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Sidebar.IsOpen") { Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVis") });
        }
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen && ViewModel.ActiveTab?.Core?.ContainsFullScreenElement == true)
        {
            _ = ViewModel.ActiveTab.Core.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
        }
        SetFullScreen(!_isFullScreen);
    }

    // ======================================================================================
    // Address bar
    // ======================================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveTab)) return;
        if (_observedTab is not null) _observedTab.PropertyChanged -= OnActiveTabPropertyChanged;
        _observedTab = ViewModel.ActiveTab;
        if (_observedTab is not null) _observedTab.PropertyChanged += OnActiveTabPropertyChanged;
        _editingAddress = false;
        ViewModel.Omnibox.Close();
        UpdateAddressText();
        UpdateTitle();
        if (_observedTab is { IsNewTabPage: false } && !AddressBox.IsKeyboardFocused) FocusWebView();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Url))
        {
            if (!_editingAddress) UpdateAddressText();
        }
        else if (e.PropertyName == nameof(BrowserTab.Title)) UpdateTitle();
    }

    private void UpdateTitle()
    {
        var t = ViewModel.ActiveTab?.Title;
        var suffix = IsPrivate ? "RELAY — Private" : "RELAY";
        Title = string.IsNullOrWhiteSpace(t) ? suffix : $"{t} — {suffix}";
    }

    private static string DisplayUrl(string full)
    {
        if (full.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) full = full[8..];
        if (full.EndsWith('/') && full.Count(c => c == '/') == 1) full = full[..^1];
        return full;
    }

    private void UpdateAddressText()
    {
        var full = ViewModel.ActiveTab?.AddressText ?? string.Empty;
        SetAddressText(AddressBox.IsKeyboardFocused ? full : DisplayUrl(full));
    }

    private void SetAddressText(string text)
    {
        _settingAddress = true;
        AddressBox.Text = text;
        _settingAddress = false;
    }

    public void FocusAddress()
    {
        if (_isFullScreen) return;
        AddressBox.Focus();
        Keyboard.Focus(AddressBox);
        AddressBox.SelectAll();
    }

    private void FocusWebView()
    {
        var wv = ViewModel.ActiveTab?.WebView;
        if (wv is not null) Dispatcher.BeginInvoke(() => wv.Focus(), DispatcherPriority.Input);
    }

    private void OnAddressMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Focus();
            e.Handled = true; // first click selects everything, like other browsers
        }
    }

    private void OnAddressGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Omnibox.BorderBrush = (Brush)FindResource("Brush.Accent");
        Omnibox.Background = (Brush)FindResource("Brush.FieldFocus");
        if (!_editingAddress) SetAddressText(ViewModel.ActiveTab?.AddressText ?? string.Empty);
        Dispatcher.BeginInvoke(AddressBox.SelectAll, DispatcherPriority.Input);
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Omnibox.ClearValue(Border.BorderBrushProperty);
        Omnibox.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        Omnibox.BorderBrush = Brushes.Transparent;
        _editingAddress = false;
        ViewModel.Omnibox.Close();
        UpdateAddressText();
    }

    private async void OnAddressTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingAddress || !AddressBox.IsKeyboardFocused) return;
        _editingAddress = true;
        await ViewModel.Omnibox.UpdateAsync(AddressBox.Text);
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        var omni = ViewModel.Omnibox;
        switch (e.Key)
        {
            case Key.Enter:
            {
                e.Handled = true;
                var text = AddressBox.Text.Trim();
                if (Keyboard.Modifiers == ModifierKeys.Control && !text.Contains('.') && !text.Contains(' ') && text.Length > 0)
                    text = $"www.{text}.com";
                var target = omni.Selected?.Target;
                var newTab = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                CommitAddress(target, text, newTab);
                break;
            }
            case Key.Escape:
                e.Handled = true;
                if (omni.IsOpen) omni.Close();
                else
                {
                    _editingAddress = false;
                    UpdateAddressText();
                    FocusWebView();
                }
                break;
            case Key.Down:
                e.Handled = true;
                omni.MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                omni.MoveSelection(-1);
                break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.Shift && omni.Selected is { Kind: SuggestionKind.History } sel:
                e.Handled = true;
                _ = RemoveHistorySuggestionAsync(sel);
                break;
        }
    }

    private async Task RemoveHistorySuggestionAsync(Suggestion sel)
    {
        var matches = await _s.History.SearchAsync(sel.Target, 5);
        foreach (var m in matches.Where(m => m.Url == sel.Target)) await _s.History.DeleteAsync(m.Id);
        ViewModel.Omnibox.Suggestions.Remove(sel);
    }

    private void CommitAddress(string? target, string text, bool newTab)
    {
        ViewModel.Omnibox.Close();
        _editingAddress = false;
        string? url = target;
        if (url is null)
        {
            var parsed = AddressParser.Parse(text, _s.SearchEngine);
            if (parsed is null) return;
            url = parsed.Uri.AbsoluteUri;
        }
        if (newTab) ViewModel.OpenInNewTab(url);
        else ViewModel.GoTo(url);
        SetAddressText(DisplayUrl(UrlDisplay.ForAddressBar(url)));
        FocusWebView();
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Suggestion s)
        {
            e.Handled = true;
            CommitAddress(s.Target, s.Primary, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    private void OnZoomChipClick(object sender, RoutedEventArgs e) => ViewModel.ZoomReset();

    // ======================================================================================
    // Keyboard shortcuts
    // ======================================================================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        var ctrl = m.HasFlag(ModifierKeys.Control);
        var shift = m.HasFlag(ModifierKeys.Shift);
        var alt = m.HasFlag(ModifierKeys.Alt);
        var vm = ViewModel;
        var handled = true;

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.T when shift: vm.ReopenClosedTab(); break;
                case Key.T: vm.NewTab(); break;
                case Key.W:
                case Key.F4: vm.CloseTab(null); break;
                case Key.Tab when shift:
                case Key.PageUp: vm.PreviousTab(); break;
                case Key.Tab:
                case Key.PageDown: vm.NextTab(); break;
                case Key.L: FocusAddress(); break;
                case Key.K: OpenPalette(); break;
                case Key.R: vm.Reload(); break;
                case Key.D: OnStarClick(this, new RoutedEventArgs()); break;
                case Key.H: OpenPanel(SidebarPanel.History); break;
                case Key.J: OpenPanel(SidebarPanel.Downloads); break;
                case Key.O when shift: OpenPanel(SidebarPanel.Bookmarks); break;
                case Key.B when shift: ToggleSidebar(); break;
                case Key.N when shift: vm.NewPrivateWindow(); break;
                case Key.N: vm.NewWindow(); break;
                case Key.P: _ = vm.ActiveTab?.PrintAsync(); break;
                case Key.F when !shift: _ = vm.ActiveTab?.FindAsync(); break;
                case Key.OemPlus:
                case Key.Add: vm.ZoomIn(); break;
                case Key.OemMinus:
                case Key.Subtract: vm.ZoomOut(); break;
                case Key.D0:
                case Key.NumPad0: vm.ZoomReset(); break;
                case >= Key.D1 and <= Key.D9: vm.SelectTabNumber(key - Key.D0); break;
                case >= Key.NumPad1 and <= Key.NumPad9: vm.SelectTabNumber(key - Key.NumPad0); break;
                default: handled = false; break;
            }
        }
        else if (alt && !ctrl)
        {
            switch (key)
            {
                case Key.Left: vm.Back(); break;
                case Key.Right: vm.Forward(); break;
                case Key.Home: vm.Home(); break;
                case Key.D: FocusAddress(); break;
                case Key.F: OnMenuClick(MenuButton, new RoutedEventArgs()); break;
                default: handled = false; break;
            }
        }
        else if (m == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.F5: vm.Reload(); break;
                case Key.F6: FocusAddress(); break;
                case Key.F11: ToggleFullScreen(); break;
                case Key.F9: _ = ToggleReaderAsync(); break;
                case Key.BrowserBack: vm.Back(); break;
                case Key.BrowserForward: vm.Forward(); break;
                case Key.BrowserRefresh: vm.Reload(); break;
                case Key.Escape when _isFullScreen && vm.ActiveTab?.Core?.ContainsFullScreenElement != true: SetFullScreen(false); break;
                default: handled = false; break;
            }
        }
        else if (m == ModifierKeys.Shift && key == Key.Escape) App.Current.ShowTaskManager();
        else handled = false;

        if (handled) e.Handled = true;
    }

    // ======================================================================================
    // Tab strip: selection, drag-to-reorder, context menu, middle-click
    // ======================================================================================

    private BrowserTab? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    private static BrowserTab? TabFrom(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: BrowserTab t }) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static bool IsInsideButton(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ItemsControl)
        {
            if (d is ButtonBase) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    private ItemsControl? _dragList;

    private void OnTabsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _previewTimer.Stop();
        TabPreviewPopup.IsOpen = false;
        if (IsInsideButton(e.OriginalSource)) return;
        var tab = TabFrom(e.OriginalSource);
        if (tab is null) return;
        ViewModel.ActiveTab = tab;
        _dragList = sender as ItemsControl;
        _dragTab = tab;
        _dragStart = e.GetPosition(_dragList);
        _dragging = false;
        if (e.ClickCount == 2) e.Handled = true; // don't maximize when double-clicking a tab
    }

    // ---- Hover previews ------------------------------------------------------------------

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private BrowserTab? _hoverTab;

    private void OnTabsMouseEnter(object sender, MouseEventArgs e)
    {
        // Refresh the active tab's thumbnail so its preview is current when the user moves away.
        _ = ViewModel.ActiveTab?.CaptureThumbnailAsync();
    }

    private void OnTabsMouseLeave(object sender, MouseEventArgs e)
    {
        _previewTimer.Stop();
        _hoverTab = null;
        TabPreviewPopup.IsOpen = false;
    }

    private void TrackHover(MouseEventArgs e)
    {
        if (!_s.Settings.Current.TabHoverPreviews || _dragging) { TabPreviewPopup.IsOpen = false; return; }
        var tab = TabFrom(e.OriginalSource);
        if (ReferenceEquals(tab, _hoverTab)) return;
        _hoverTab = tab;
        TabPreviewPopup.IsOpen = false;
        _previewTimer.Stop();
        if (tab is null) return;
        if (_previewTimer.Tag is null)
        {
            _previewTimer.Tag = true;
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        }
        _previewTimer.Start();
    }

    private void ShowPreview()
    {
        var tab = _hoverTab;
        if (tab is null || !TabsList.IsMouseOver) return;
        if (TabsList.ItemContainerGenerator.ContainerFromItem(tab) is not UIElement container) return;
        PreviewTitle.Text = tab.Title;
        PreviewHost.Text = tab.IsNewTabPage ? "New tab" : UrlDisplay.Short(tab.Url);
        PreviewState.Text = tab.IsDiscarded ? "Unloaded to save memory · reloads when opened" : tab.IsSleeping ? "Sleeping to save memory" : tab.IsPlayingAudio ? "Playing audio" : string.Empty;
        PreviewState.Visibility = PreviewState.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The active tab is already on screen, so its preview only shows the text.
        var showImage = tab.Thumbnail is not null && !ReferenceEquals(tab, ViewModel.ActiveTab);
        PreviewImage.Source = showImage ? tab.Thumbnail : null;
        PreviewImageBox.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        TabPreviewPopup.PlacementTarget = container;
        TabPreviewPopup.IsOpen = true;
    }

    private void OnTabsMouseMove(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, TabsList)) TrackHover(e);
        if (_dragTab is null || _dragList is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_dragList);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6) return;
            _dragging = true;
            TabPreviewPopup.IsOpen = false;
            _dragList.CaptureMouse();
        }
        // Whatever strip item is under the pointer becomes the drop slot.
        if (_dragList.InputHitTest(pos) is DependencyObject hit && ItemFrom(hit) is { } target && !ReferenceEquals(target, _dragTab))
            ViewModel.MoveTabTo(_dragTab, target);
    }

    private static object? ItemFrom(DependencyObject d)
    {
        DependencyObject? cur = d;
        while (cur is not null)
        {
            if (cur is FrameworkElement { DataContext: BrowserTab or TabGroup } fe) return fe.DataContext;
            cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void OnTabsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) { _dragList?.ReleaseMouseCapture(); e.Handled = true; }
        _dragTab = null;
        _dragList = null;
        _dragging = false;
    }

    private void OnTabsMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (TabFrom(e.OriginalSource) is { } tab) { ViewModel.CloseTab(tab); e.Handled = true; }
    }

    private void OnTabsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) ViewModel.PreviousTab(); else ViewModel.NextTab();
        e.Handled = true;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        var container = (VTabsList.ItemContainerGenerator.ContainerFromItem(tab) ?? TabsList.ItemContainerGenerator.ContainerFromItem(tab)) as FrameworkElement;
        if (container is not { IsVisible: true }) { ViewModel.CloseTab(tab); return; }
        // Fade the tab out before it is removed.
        container.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) =>
        {
            ViewModel.CloseTab(tab);
            container.BeginAnimation(OpacityProperty, null);
            container.IsHitTestVisible = true;
        };
        container.BeginAnimation(OpacityProperty, fade);
    }

    private void OnPinnedMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TabFrom(e.OriginalSource) is not { } tab) return;
        ViewModel.ActiveTab = tab;
        e.Handled = true;
    }

    private void OnTabMuteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) tab.ToggleMute();
    }

    private void OnCrashReloadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) tab.Reload();
    }

    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        if ((sender as FrameworkElement)?.DataContext is TabGroup g) { ViewModel.ToggleCollapse(g); e.Handled = true; }
    }

    private void ShowGroupMenu(TabGroup g, UIElement target)
    {
        var vm = ViewModel;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Rename group…", Icons.Edit, () => RenameGroup(g)));
        menu.Items.Add(Item(g.IsCollapsed ? "Expand group" : "Collapse group", g.IsCollapsed ? Icons.ChevronRight : Icons.ChevronLeft, () => vm.ToggleCollapse(g)));
        menu.Items.Add(Item("New tab in group", Icons.Add, () =>
        {
            var t = vm.AddTab(null, activate: true);
            vm.AddToGroup(t, g);
        }));
        menu.Items.Add(new Separator());
        foreach (var (name, _) in TabGroup.Palette)
        {
            var colorName = name;
            menu.Items.Add(Item(name, g.ColorName == name ? Icons.Check : "", () => { g.ColorName = colorName; vm.RebuildStrip(); }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ungroup", "", () => vm.Ungroup(g)));
        menu.Items.Add(Item("Close group", Icons.Close, () => vm.CloseGroup(g)));
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void RenameGroup(TabGroup g)
    {
        var d = new RelayDialog(this, "Rename group", null, Icons.Edit)
            .AddInput("name", "Name", g.Name)
            .AddButton("cancel", "Cancel", isCancel: true)
            .AddButton("ok", "Save", primary: true);
        if (d.Ask() == "ok") { g.Name = d.Input("name"); ViewModel.RebuildStrip(); }
    }

    private void NewGroupFor(BrowserTab tab)
    {
        var colors = TabGroup.Palette.Select(p => ((object)p.Name, p.Name)).ToList();
        var used = ViewModel.Groups.Select(g => g.ColorName).ToHashSet();
        var suggested = TabGroup.Palette.Select(p => p.Name).FirstOrDefault(n => n != "Grey" && !used.Contains(n)) ?? "Blue";
        var d = new RelayDialog(this, "New tab group", null, Icons.Add)
            .AddInput("name", "Name (optional)", string.Empty)
            .AddChoice("Colour", colors, suggested)
            .AddButton("cancel", "Cancel", isCancel: true)
            .AddButton("ok", "Create group", primary: true);
        if (d.Ask() != "ok") return;
        ViewModel.CreateGroup(tab, d.Input("name"), d.Choice as string ?? suggested);
    }

    private void OnTabsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom((DependencyObject)e.OriginalSource) is TabGroup group)
        {
            e.Handled = true;
            ShowGroupMenu(group, (UIElement)sender);
            return;
        }
        var tab = TabFrom(e.OriginalSource);
        if (tab is null) return;
        e.Handled = true;
        var vm = ViewModel;
        var index = vm.Tabs.IndexOf(tab);
        var menu = new ContextMenu();
        menu.Items.Add(Item("New tab to the right", Icons.Add, () => vm.AddTab(null, true, index + 1), "Ctrl+T"));
        menu.Items.Add(Item(tab.IsPinned ? "Unpin tab" : "Pin tab", tab.IsPinned ? "\uE77A" : "\uE718", () => vm.TogglePin(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reload", Icons.Refresh, tab.Reload, "Ctrl+R"));
        menu.Items.Add(Item("Duplicate", Icons.Duplicate, () => vm.DuplicateTab(tab)));
        menu.Items.Add(Item(tab.IsMuted ? "Unmute site" : "Mute site", tab.IsMuted ? Icons.Volume : Icons.Mute, tab.ToggleMute, enabled: tab.IsCreated));
        menu.Items.Add(Item("Add to reading list", Icons.ReadingList, () => _ = vm.AddToReadingListAsync(tab), enabled: !tab.IsNewTabPage));
        menu.Items.Add(Item("Copy link", Icons.Link, () => TrySetClipboard(tab.Url), enabled: !tab.IsNewTabPage));
        menu.Items.Add(Item("Put tab to sleep", "\uE708", () => _ = tab.SleepNowAsync(), enabled: tab.IsCreated && !tab.IsVisibleTab && !tab.IsSleeping));
        menu.Items.Add(Item("Unload tab to free memory", "\uE74D", () => tab.Discard(), enabled: tab.CanDiscard));
        menu.Items.Add(new Separator());
        // Split view
        if (vm.IsSplit && (ReferenceEquals(tab, vm.SplitLeft) || ReferenceEquals(tab, vm.SplitRight)))
        {
            menu.Items.Add(Item("Swap split sides", "\uE8AB", vm.SwapSplit));
            menu.Items.Add(Item("Close split view", "\uE89F", vm.CloseSplit));
        }
        else if (ReferenceEquals(tab, vm.ActiveTab)) menu.Items.Add(Item("Split view with a new tab", "\uE89F", vm.SplitWithNewTab));
        else menu.Items.Add(Item("Open next to current tab", "\uE89F", () => vm.OpenSplit(tab)));
        // Groups
        menu.Items.Add(Item("Add tab to new group…", "\uE8F4", () => NewGroupFor(tab)));
        foreach (var g in vm.Groups.Where(g => !ReferenceEquals(g, tab.Group)).Take(8))
        {
            var target = g;
            var label = string.IsNullOrWhiteSpace(g.Name) ? $"{g.ColorName} group" : g.Name;
            menu.Items.Add(Item($"Add to “{label}”", "", () => vm.AddToGroup(tab, target)));
        }
        if (tab.Group is not null) menu.Items.Add(Item("Remove from group", "", () => vm.RemoveFromGroup(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", Icons.Close, () => vm.CloseTab(tab), "Ctrl+W"));
        menu.Items.Add(Item("Close other tabs", "", () => vm.CloseOtherTabs(tab), enabled: vm.Tabs.Count > 1));
        menu.Items.Add(Item("Close tabs to the right", "", () => vm.CloseTabsToRight(tab), enabled: index < vm.Tabs.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reopen closed tab", Icons.Retry, vm.ReopenClosedTab, "Ctrl+Shift+T", vm.CanReopenClosed));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string glyph, Action action, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Icon = glyph, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ======================================================================================
    // Toolbar actions
    // ======================================================================================

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        var menu = new ContextMenu();
        menu.Items.Add(Item("New tab", Icons.Add, vm.NewTab, "Ctrl+T"));
        menu.Items.Add(Item("New window", Icons.NewWindow, vm.NewWindow, "Ctrl+N"));
        menu.Items.Add(Item("New private window", Icons.Private, vm.NewPrivateWindow, "Ctrl+Shift+N"));
        menu.Items.Add(new Separator());
        menu.Items.Add(ZoomRow());
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Bookmarks", Icons.Bookmarks, () => OpenPanel(SidebarPanel.Bookmarks), "Ctrl+Shift+O"));
        menu.Items.Add(Item("History", Icons.History, () => OpenPanel(SidebarPanel.History), "Ctrl+H"));
        menu.Items.Add(Item("Downloads", Icons.Download, () => OpenPanel(SidebarPanel.Downloads), "Ctrl+J"));
        menu.Items.Add(Item("Reading list", Icons.ReadingList, () => OpenPanel(SidebarPanel.ReadingList)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Find on page", Icons.Find, () => _ = vm.ActiveTab?.FindAsync(), "Ctrl+F", vm.ActiveTab?.IsCreated == true));
        menu.Items.Add(Item("Print", Icons.Print, () => _ = vm.ActiveTab?.PrintAsync(), "Ctrl+P", vm.ActiveTab?.IsCreated == true));
        var webPage = vm.ActiveTab is { IsNewTabPage: false, IsCreated: true };
        menu.Items.Add(Item(vm.ActiveTab?.IsReaderPage == true ? "Leave reader view" : "Reader view", "\uE736", () => _ = ToggleReaderAsync(), "F9", webPage));
        menu.Items.Add(Item("Screenshot…", "\uE722", () => ShowScreenshotMenu(MenuButton), enabled: webPage));
        menu.Items.Add(Item("Picture-in-picture", "\uE8A7", () => _ = TogglePipAsync(), enabled: webPage));
        menu.Items.Add(Item("QR code for this page", "\uED14", ShowQr, enabled: webPage));
        menu.Items.Add(Item("Full screen", Icons.FullScreen, ToggleFullScreen, "F11"));
        menu.Items.Add(vm.IsSplit
            ? Item("Close split view", "\uE89F", vm.CloseSplit)
            : Item("Split view", "\uE89F", vm.SplitWithNewTab, enabled: vm.ActiveTab is not null));
        menu.Items.Add(Item("Vertical tabs", vm.VerticalTabs ? Icons.Check : "\uE8A4", ToggleVerticalTabs));
        menu.Items.Add(Item("Command palette", Icons.Search, OpenPalette, "Ctrl+K"));
        menu.Items.Add(Item("Developer tools", Icons.Code, () => vm.ActiveTab?.OpenDevTools(), "F12", vm.ActiveTab?.IsCreated == true));
        menu.Items.Add(Item("Task manager", "\uE9D9", () => App.Current.ShowTaskManager(), "Shift+Esc"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clear browsing data…", Icons.Clear, () => OnClearData(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Settings", Icons.Settings, () => App.Current.ShowSettings()));
        menu.Items.Add(Item("About RELAY", Icons.Info, () => App.Current.ShowSettings("about")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", Icons.Exit, () => App.Current.WindowManager?.CloseAll()));
        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 10, target.Height + 4), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    private MenuItem ZoomRow()
    {
        var vm = ViewModel;
        var label = new TextBlock { Text = vm.ZoomText, Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Button Btn(string glyph, string tip, Action a)
        {
            var b = new Button { Content = glyph, Style = (Style)FindResource("IconButton.Small"), ToolTip = tip };
            b.Click += (_, _) => { a(); label.Text = vm.ZoomText; };
            return b;
        }
        var panel = new DockPanel { LastChildFill = false, Width = 236 };
        var title = new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Btn(Icons.ZoomOut, "Zoom out (Ctrl+−)", vm.ZoomOut));
        controls.Children.Add(label);
        controls.Children.Add(Btn(Icons.ZoomIn, "Zoom in (Ctrl++)", vm.ZoomIn));
        DockPanel.SetDock(controls, Dock.Right);
        panel.Children.Add(title);
        panel.Children.Add(controls);
        return new MenuItem { Header = panel, Icon = Icons.ZoomIn, StaysOpenOnClick = true, IsEnabled = vm.ActiveTab?.IsCreated == true };
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (!ViewModel.SidebarEnabled)
        {
            // The rail is hidden in settings: the toolbar button still opens/closes panels.
            ViewModel.Sidebar.Panel = ViewModel.Sidebar.IsOpen ? SidebarPanel.None : SidebarPanel.Bookmarks;
            return;
        }
        ViewModel.Sidebar.Panel = ViewModel.Sidebar.IsOpen ? SidebarPanel.None : SidebarPanel.Bookmarks;
    }

    private void OpenPanel(SidebarPanel panel)
    {
        if (_isFullScreen) SetFullScreen(false);
        ViewModel.Sidebar.Toggle(panel);
    }

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SidebarPanel>(tag, out var p))
            ViewModel.Sidebar.Toggle(p);
    }

    private void OnCloseSidePanel(object sender, RoutedEventArgs e) => ViewModel.Sidebar.Panel = SidebarPanel.None;
    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
    private void OnDownloadsClick(object sender, RoutedEventArgs e) => OpenPanel(SidebarPanel.Downloads);

    private async void OnAddToReadingList(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null || tab.IsNewTabPage) return;
        await ViewModel.AddToReadingListAsync();
        if (ViewModel.Sidebar.Panel != SidebarPanel.ReadingList) ViewModel.Sidebar.Panel = SidebarPanel.ReadingList;
    }

    // ======================================================================================
    // Bookmark popup (star / Ctrl+D)
    // ======================================================================================

    private sealed record FolderChoice(long? Id, string Text);

    private async void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen) return;
        var existed = ViewModel.IsBookmarked;
        var b = await ViewModel.EnsureBookmarkAsync();
        if (b is null) return;
        _editingBookmark = b;

        var all = await _s.Bookmarks.GetAllAsync();
        var choices = new List<FolderChoice> { new(null, "Bookmarks") };
        void Add(long? parent, string prefix, int depth)
        {
            if (depth > 12) return;
            foreach (var f in all.Where(x => x.IsFolder && x.ParentId == parent).OrderBy(x => x.Position))
            {
                choices.Add(new FolderChoice(f.Id, prefix + f.Title));
                Add(f.Id, prefix + "    ", depth + 1);
            }
        }
        Add(null, "    ", 0);
        BookmarkFolder.ItemsSource = choices;
        BookmarkFolder.SelectedItem = choices.FirstOrDefault(c => c.Id == b.ParentId) ?? choices[0];
        BookmarkName.Text = b.Title;
        BookmarkPopupTitle.Text = existed ? "Edit bookmark" : "Bookmark added";
        BookmarkPopup.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { BookmarkName.Focus(); BookmarkName.SelectAll(); }, DispatcherPriority.Input);
    }

    private async Task SaveBookmarkEditsAsync()
    {
        var b = _editingBookmark;
        _editingBookmark = null;
        if (b is null) return;
        var title = BookmarkName.Text.Trim();
        if (title.Length == 0) title = b.Title;
        var folder = (BookmarkFolder.SelectedItem as FolderChoice)?.Id;
        if (title != b.Title || folder != b.ParentId)
            await _s.Bookmarks.UpdateAsync(b.Id, title, b.Url, folder);
    }

    private async void OnBookmarkDone(object sender, RoutedEventArgs e)
    {
        await SaveBookmarkEditsAsync();
        BookmarkPopup.IsOpen = false;
    }

    private async void OnBookmarkRemove(object sender, RoutedEventArgs e)
    {
        var b = _editingBookmark;
        _editingBookmark = null;
        BookmarkPopup.IsOpen = false;
        if (b is not null) await _s.Bookmarks.DeleteAsync(b.Id);
        await ViewModel.RefreshBookmarkStateAsync();
    }

    private async void OnBookmarkPopupClosed(object? sender, EventArgs e) => await SaveBookmarkEditsAsync();

    private void OnBookmarkPopupKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnBookmarkDone(sender, e);
        else if (e.Key == Key.Escape) BookmarkPopup.IsOpen = false;
    }

    // ======================================================================================
    // Ad & tracker blocker (shield)
    // ======================================================================================

    private void OnShieldClick(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null) return;
        var site = BlockerService.SiteKey(tab.Host);
        ShieldHost.Text = site.Length > 0 ? site : tab.Host;
        ShieldCount.Text = tab.BlockedCount.ToString("N0");
        ShieldTotal.Text = $"{_s.Blocker.TotalBlocked:N0} blocked since you started using RELAY";
        ShieldToggle.IsChecked = !_s.Blocker.IsSiteAllowed(tab.Host);
        ShieldToggleText.Text = $"Block ads and trackers on {site}";
        ShieldToggle.IsEnabled = _s.Blocker.Enabled;
        ShieldOffNote.Visibility = _s.Blocker.Enabled ? Visibility.Collapsed : Visibility.Visible;
        ShieldPopup.IsOpen = true;
    }

    private void OnShieldToggle(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null) return;
        _s.Blocker.SetSiteAllowed(tab.Host, allowed: ShieldToggle.IsChecked != true);
        tab.UpdateBlockingFilter();
        tab.Reload(); // apply to the page right away
    }

    private void OnBlockerSettings(object sender, RoutedEventArgs e)
    {
        ShieldPopup.IsOpen = false;
        App.Current.ShowSettings("blocking");
    }

    // ======================================================================================
    // Site info (lock icon)
    // ======================================================================================

    public sealed record PermissionRow(string Origin, string Kind, string Display);

    private async void OnSiteInfoClick(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null) return;
        if (tab.IsNewTabPage) { FocusAddress(); return; }

        SiteInfoHost.Text = tab.Host.Length > 0 ? tab.Host : UrlDisplay.Short(tab.Url);
        SiteInfoGlyph.Text = (string)((System.Windows.Data.IValueConverter)FindResource("SecurityGlyph")).Convert(tab.Security, typeof(string), null!, null!);
        (SiteInfoStatus.Text, SiteInfoDetail.Text) = tab.Security switch
        {
            SecurityState.Secure => ("Connection is secure", "Information you send to this site (like passwords or card numbers) is encrypted on its way."),
            SecurityState.NotSecure => ("Not secure", "This site doesn't use an encrypted connection. Avoid entering passwords or payment details."),
            SecurityState.File => ("Local file", "This page is a file stored on this computer."),
            _ => ("RELAY page", string.Empty),
        };
        SiteInfoStatus.Foreground = (Brush)FindResource(tab.Security == SecurityState.NotSecure ? "Brush.Danger" : "Brush.Text");

        var origin = UrlDisplay.Origin(tab.Url);
        var rows = new List<PermissionRow>();
        if (!IsPrivate && origin.Length > 0)
        {
            foreach (var p in (await _s.Permissions.GetAllAsync()).Where(p => p.Origin == origin))
                rows.Add(new PermissionRow(p.Origin, p.Kind, $"{p.Kind}: {(p.Decision == PermissionDecision.Allow ? "Allowed" : "Blocked")}"));
        }
        SiteInfoPerms.ItemsSource = rows;
        SiteInfoPermsHeader.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SiteInfoPopup.IsOpen = true;
    }

    private async void OnSitePermissionReset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PermissionRow row) return;
        await _s.Permissions.SetAsync(row.Origin, row.Kind, PermissionDecision.Ask);
        SiteInfoPerms.ItemsSource = ((IEnumerable<PermissionRow>)SiteInfoPerms.ItemsSource).Where(r => r != row).ToList();
    }

    private void OnSiteSettings(object sender, RoutedEventArgs e)
    {
        SiteInfoPopup.IsOpen = false;
        App.Current.ShowSettings("privacy");
    }

    // ======================================================================================
    // Downloads
    // ======================================================================================

    public void ShowDownloadNotice(DownloadItem item, bool finished)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _isFullScreen) return;
        if (ViewModel.Sidebar.Panel == SidebarPanel.Downloads) return; // already visible
        DownloadPopupTitle.Text = finished ? "Download complete" : "Downloading";
        DownloadPopupItem.Content = item;
        DownloadPopup.IsOpen = true;
        _downloadPopupTimer.Stop();
        _downloadPopupTimer.Interval = TimeSpan.FromSeconds(finished ? 6 : 4);
        _downloadPopupTimer.Start();
    }

    private void OnDownloadPopupClose(object sender, RoutedEventArgs e) => DownloadPopup.IsOpen = false;

    private void OnShowAllDownloads(object sender, RoutedEventArgs e)
    {
        DownloadPopup.IsOpen = false;
        ViewModel.Sidebar.Panel = SidebarPanel.Downloads;
    }

    private async void OnDownloadAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadItem item, Tag: string action }) return;
        try
        {
            switch (action)
            {
                case "pause": item.Pause(); break;
                case "resume": item.Resume(); break;
                case "cancel": item.Cancel(); break;
                case "retry": ViewModel.OpenInNewTab(item.Url, activate: false); break;
                case "folder": DownloadManager.ShowInFolder(item); break;
                case "remove": await _s.Downloads.RemoveAsync(item); break;
                case "open":
                    if (!File.Exists(item.FilePath))
                    {
                        RelayDialog.Info(this, "File not found", "The file was moved or deleted.", Icons.Warning);
                        break;
                    }
                    if (Relay.Core.Downloads.FileNameSafety.IsDangerous(item.FilePath) &&
                        !RelayDialog.Confirm(this, "Open this file?", $"“{item.FileName}” can run programs on your computer. Open it only if you trust it.", "Open", danger: true, glyph: Icons.Warning))
                        break;
                    DownloadManager.Open(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Download action", ex);
            RelayDialog.Info(this, "Couldn't complete that", ex.Message, Icons.Warning);
        }
    }

    private void OnOpenDownloadsFolder(object sender, RoutedEventArgs e) => App.OpenExternal(_s.DownloadDirectory);
    private async void OnClearDownloads(object sender, RoutedEventArgs e) => await _s.Downloads.ClearFinishedAsync();

    // ======================================================================================
    // Sidebar: history
    // ======================================================================================

    private void OpenUrl(string? url, bool newTab)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (newTab) ViewModel.OpenInNewTab(url, activate: false);
        else ViewModel.GoTo(url);
    }

    private void OnHistoryActivate(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow r) OpenUrl(r.Url, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private async void OnHistoryKey(object sender, KeyEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow r) return;
        if (e.Key == Key.Enter) OpenUrl(r.Url, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        else if (e.Key == Key.Delete) await ViewModel.Sidebar.History.DeleteAsync(r);
    }

    private void OnHistoryMiddle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (e.OriginalSource as FrameworkElement)?.DataContext is HistoryRow r) OpenUrl(r.Url, true);
    }

    private void OnHistoryContextMenu(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not HistoryRow r) { e.Handled = true; return; }
        var menu = HistoryList.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item("Open", Icons.Globe, () => OpenUrl(r.Url, false)));
        menu.Items.Add(Item("Open in new tab", Icons.Add, () => OpenUrl(r.Url, true)));
        menu.Items.Add(Item("Copy link", Icons.Link, () => TrySetClipboard(r.Url)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete from history", Icons.Delete, () => _ = ViewModel.Sidebar.History.DeleteAsync(r), "Del"));
    }

    private async void OnHistoryMore(object sender, RoutedEventArgs e) => await ViewModel.Sidebar.History.LoadMoreAsync();

    private async void OnClearData(object sender, RoutedEventArgs e)
    {
        await ClearDataDialog.ShowAsync(this, _s);
        if (ViewModel.Sidebar.Panel == SidebarPanel.History) await ViewModel.Sidebar.History.LoadAsync();
    }

    // ======================================================================================
    // Sidebar: bookmarks
    // ======================================================================================

    private void OnBookmarkActivate(object sender, MouseButtonEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkRow r) ActivateBookmark(r, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void ActivateBookmark(BookmarkRow r, bool newTab)
    {
        if (r.IsFolder) ViewModel.Sidebar.Bookmarks.Open(r.Bookmark);
        else OpenUrl(r.Url, newTab);
    }

    private async void OnBookmarkKey(object sender, KeyEventArgs e)
    {
        if (BookmarksList.SelectedItem is not BookmarkRow r)
        {
            if (e.Key == Key.Back) ViewModel.Sidebar.Bookmarks.Up();
            return;
        }
        switch (e.Key)
        {
            case Key.Enter: ActivateBookmark(r, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); break;
            case Key.Delete: await DeleteBookmarkAsync(r); break;
            case Key.F2: await EditBookmarkAsync(r); break;
            case Key.Back: ViewModel.Sidebar.Bookmarks.Up(); break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.Alt: await _s.Bookmarks.ReorderAsync(r.Bookmark.Id, r.Bookmark.Position - 1); e.Handled = true; break;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.Alt: await _s.Bookmarks.ReorderAsync(r.Bookmark.Id, r.Bookmark.Position + 1); e.Handled = true; break;
        }
    }

    private void OnBookmarkMiddle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (e.OriginalSource as FrameworkElement)?.DataContext is BookmarkRow { IsFolder: false } r)
            OpenUrl(r.Url, true);
    }

    private void OnBookmarksUp(object sender, RoutedEventArgs e) => ViewModel.Sidebar.Bookmarks.Up();

    private void OnBookmarkContextMenu(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BookmarkRow r) { e.Handled = true; return; }
        var menu = BookmarksList.ContextMenu!;
        menu.Items.Clear();
        if (r.IsFolder)
        {
            menu.Items.Add(Item("Open folder", Icons.FolderOpen, () => ViewModel.Sidebar.Bookmarks.Open(r.Bookmark)));
            menu.Items.Add(Item("Open all in new tabs", Icons.Add, () => _ = OpenFolderAsync(r.Bookmark)));
        }
        else
        {
            menu.Items.Add(Item("Open", Icons.Globe, () => OpenUrl(r.Url, false)));
            menu.Items.Add(Item("Open in new tab", Icons.Add, () => OpenUrl(r.Url, true)));
            menu.Items.Add(Item("Open in private window", Icons.Private, () => App.Current.WindowManager?.Open(true, urls: [r.Url!])));
            menu.Items.Add(Item("Copy link", Icons.Link, () => TrySetClipboard(r.Url!)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Edit…", Icons.Edit, () => _ = EditBookmarkAsync(r), "F2"));
        menu.Items.Add(Item("Move up", Icons.ChevronLeft, () => _ = _s.Bookmarks.ReorderAsync(r.Bookmark.Id, r.Bookmark.Position - 1), "Alt+Up"));
        menu.Items.Add(Item("Move down", Icons.ChevronRight, () => _ = _s.Bookmarks.ReorderAsync(r.Bookmark.Id, r.Bookmark.Position + 1), "Alt+Down"));
        menu.Items.Add(Item("Delete", Icons.Delete, () => _ = DeleteBookmarkAsync(r), "Del"));
    }

    private async Task OpenFolderAsync(Bookmark folder)
    {
        var all = await _s.Bookmarks.GetAllAsync();
        var urls = all.Where(b => b.ParentId == folder.Id && !b.IsFolder && b.Url is not null).OrderBy(b => b.Position).Select(b => b.Url!).ToList();
        if (urls.Count > 15 && !RelayDialog.Confirm(this, "Open all bookmarks?", $"This opens {urls.Count} tabs.", "Open all")) return;
        foreach (var u in urls) ViewModel.OpenInNewTab(u, activate: false);
    }

    private async Task DeleteBookmarkAsync(BookmarkRow r)
    {
        if (r.IsFolder && r.ChildCount > 0 &&
            !RelayDialog.Confirm(this, $"Delete “{r.Title}”?", $"This folder and the {r.ChildCount} item(s) in it will be deleted.", "Delete", danger: true))
            return;
        await _s.Bookmarks.DeleteAsync(r.Bookmark.Id);
        await ViewModel.RefreshBookmarkStateAsync();
    }

    private async Task EditBookmarkAsync(BookmarkRow r)
    {
        var all = await _s.Bookmarks.GetAllAsync();
        var folders = new List<(object Value, string Text)> { (0L, "Bookmarks") };
        foreach (var f in all.Where(b => b.IsFolder && b.Id != r.Bookmark.Id).OrderBy(b => b.Title))
            folders.Add((f.Id, ViewModel.Sidebar.Bookmarks.FolderPath(f.Id)));

        var d = new RelayDialog(this, r.IsFolder ? "Edit folder" : "Edit bookmark", null, r.IsFolder ? Icons.Folder : Icons.Star)
            .AddInput("name", "Name", r.Title);
        if (!r.IsFolder) d.AddInput("url", "URL", r.Url ?? string.Empty);
        d.AddChoice("Folder", folders, r.Bookmark.ParentId ?? 0L)
         .AddButton("cancel", "Cancel", isCancel: true)
         .AddButton("save", "Save", primary: true);
        d.Validate = dlg =>
        {
            if (dlg.Input("name").Length == 0) return "Enter a name.";
            if (!r.IsFolder && AddressParser.Parse(dlg.Input("url"), _s.SearchEngine) is not { IsSearch: false }) return "Enter a valid web address.";
            return null;
        };
        if (d.Ask() != "save") return;
        var url = r.IsFolder ? null : AddressParser.Parse(d.Input("url"), _s.SearchEngine)!.Uri.AbsoluteUri;
        var parent = d.Choice is long id && id > 0 ? id : (long?)null;
        await _s.Bookmarks.UpdateAsync(r.Bookmark.Id, d.Input("name"), url, parent);
        await ViewModel.RefreshBookmarkStateAsync();
    }

    private async void OnBookmarkCurrent(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null || tab.IsNewTabPage) return;
        var existing = await _s.Bookmarks.FindByUrlAsync(tab.Url);
        if (existing is null)
            await _s.Bookmarks.AddAsync(string.IsNullOrWhiteSpace(tab.Title) ? tab.Host : tab.Title, tab.Url, ViewModel.Sidebar.Bookmarks.Folder?.Id);
        await ViewModel.RefreshBookmarkStateAsync();
    }

    private async void OnNewBookmarkFolder(object sender, RoutedEventArgs e)
    {
        var d = new RelayDialog(this, "New folder", null, Icons.NewFolder)
            .AddInput("name", "Name", "New folder")
            .AddButton("cancel", "Cancel", isCancel: true)
            .AddButton("ok", "Create", primary: true);
        d.Validate = dlg => dlg.Input("name").Length == 0 ? "Enter a name." : null;
        if (d.Ask() != "ok") return;
        await _s.Bookmarks.AddAsync(d.Input("name"), null, ViewModel.Sidebar.Bookmarks.Folder?.Id, isFolder: true);
    }

    private void OnBookmarksMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(Item("Import bookmarks…", Icons.Import, () => _ = ImportBookmarksAsync()));
        menu.Items.Add(Item("Export bookmarks…", Icons.Export, () => _ = ExportBookmarksAsync()));
        menu.IsOpen = true;
    }

    private async Task ImportBookmarksAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import bookmarks",
            Filter = "Bookmark files (*.html;*.htm)|*.html;*.htm|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 50 * 1024 * 1024) throw new InvalidDataException("The file is too large to be a bookmark export.");
            var html = await File.ReadAllTextAsync(dlg.FileName);
            var tree = await Task.Run(() => NetscapeBookmarks.Parse(html));
            var count = tree.CountBookmarks();
            if (count == 0)
            {
                RelayDialog.Info(this, "No bookmarks found", "That file doesn't contain bookmarks in the standard HTML format that browsers export.", Icons.Warning);
                return;
            }
            var added = await _s.Bookmarks.ImportAsync(tree, $"Imported {DateTime.Now:yyyy-MM-dd}");
            RelayDialog.Info(this, "Bookmarks imported", $"{added} bookmarks were added to a new folder.", Icons.Check);
            ViewModel.Sidebar.Panel = SidebarPanel.Bookmarks;
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Import bookmarks", ex);
            RelayDialog.Info(this, "Import failed", ex.Message, Icons.Warning);
        }
    }

    private async Task ExportBookmarksAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export bookmarks",
            FileName = $"relay-bookmarks-{DateTime.Now:yyyy-MM-dd}.html",
            Filter = "Bookmark file (*.html)|*.html",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var tree = await _s.Bookmarks.ExportTreeAsync();
            await File.WriteAllTextAsync(dlg.FileName, NetscapeBookmarks.Export(tree));
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Export bookmarks", ex);
            RelayDialog.Info(this, "Export failed", ex.Message, Icons.Warning);
        }
    }

    // ======================================================================================
    // Sidebar: reading list
    // ======================================================================================

    private async void OnReadingActivate(object sender, MouseButtonEventArgs e)
    {
        if (ReadingList.SelectedItem is not ReadingRow r) return;
        OpenUrl(r.Url, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (!r.IsRead) await _s.ReadingList.SetReadAsync(r.Item.Id, true);
    }

    private async void OnReadingKey(object sender, KeyEventArgs e)
    {
        if (ReadingList.SelectedItem is not ReadingRow r) return;
        if (e.Key == Key.Enter) { OpenUrl(r.Url, false); await _s.ReadingList.SetReadAsync(r.Item.Id, true); }
        else if (e.Key == Key.Delete) await _s.ReadingList.DeleteAsync(r.Item.Id);
    }

    private async void OnReadingToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReadingRow r) await _s.ReadingList.SetReadAsync(r.Item.Id, !r.IsRead);
    }

    private async void OnReadingRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReadingRow r) await _s.ReadingList.DeleteAsync(r.Item.Id);
    }

    // ======================================================================================
    // Vertical tabs
    // ======================================================================================

    private void ToggleVerticalTabs() => _s.Settings.Update(s => s.VerticalTabs = !s.VerticalTabs);

    private void OnVerticalNewTab(object sender, RoutedEventArgs e) => ViewModel.NewTab();

    private void OnToggleVerticalCollapse(object sender, RoutedEventArgs e)
    {
        _hoverExpandTimer?.Stop();
        ViewModel.SidebarHoverExpanded = false;
        _s.Settings.Update(s => s.VerticalTabsCollapsed = !s.VerticalTabsCollapsed);
    }

    // Collapsed vertical tabs expand while the mouse rests on them (Zen's compact mode).
    private DispatcherTimer? _hoverExpandTimer;

    private void OnVerticalPanelEnter(object sender, MouseEventArgs e)
    {
        if (!ViewModel.VerticalTabsCollapsed) return;
        if (_hoverExpandTimer is null)
        {
            _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _hoverExpandTimer.Tick += (_, _) =>
            {
                _hoverExpandTimer.Stop();
                if (VerticalTabsPanel.IsMouseOver) ViewModel.SidebarHoverExpanded = true;
            };
        }
        _hoverExpandTimer.Start();
    }

    private void OnVerticalPanelLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer?.Stop();
        ViewModel.SidebarHoverExpanded = false;
    }

    /// <summary>
    /// Vertical tabs use the Zen-style layout: no tab strip, the toolbar doubles as the title bar,
    /// and the page floats as a rounded card on the window background.
    /// </summary>
    private void ApplyZenLayout()
    {
        var vertical = ViewModel.VerticalTabs;
        var zen = vertical && !_isFullScreen;
        StripRow.Visibility = _isFullScreen || vertical ? Visibility.Collapsed : Visibility.Visible;
        ToolbarCaption.Visibility = zen ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.SetResourceReference(Border.BackgroundProperty, zen ? "Brush.Chrome" : "Brush.Toolbar");
        Toolbar.BorderThickness = zen ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        ContentGrid.SetResourceReference(Panel.BackgroundProperty, zen ? "Brush.Chrome" : "Brush.Toolbar");
        ContentArea.Margin = zen ? new Thickness(0, 0, 8, 8) : new Thickness(0);
        ViewModel.PageCornerRadius = zen ? 10 : 0;
        ApplyDensity();
    }

    private void UpdateVerticalWidth()
    {
        var target = ViewModel.VerticalTabsNarrow ? 56.0 : 248.0;
        if (!IsLoaded) { VerticalTabsPanel.Width = target; return; }
        VerticalTabsPanel.BeginAnimation(WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ======================================================================================
    // Split view
    // ======================================================================================

    private double _splitRatio = 0.5;

    private void ApplySplitLayout()
    {
        var split = ViewModel.IsSplit;
        SetColumns(ContentArea.ColumnDefinitions, split);
        if (FindChild<Grid>(ContentHost) is { } panel) SetColumns(panel.ColumnDefinitions, split);
        SplitHandle.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.ActiveTab?.WebView?.Focus();
    }

    private void SetColumns(ColumnDefinitionCollection cols, bool split)
    {
        if (cols.Count < 3) return;
        cols[0].Width = split ? new GridLength(_splitRatio, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        cols[1].Width = split ? new GridLength(8) : new GridLength(0);
        cols[2].Width = split ? new GridLength(1 - _splitRatio, GridUnitType.Star) : new GridLength(0);
    }

    private void OnSplitDrag(object sender, RoutedEventArgs e)
    {
        var a = ContentArea.ColumnDefinitions[0].ActualWidth;
        var b = ContentArea.ColumnDefinitions[2].ActualWidth;
        if (a + b <= 0) return;
        _splitRatio = Math.Clamp(a / (a + b), 0.15, 0.85);
        ApplySplitLayout();
    }

    private void OnSplitReset(object sender, MouseButtonEventArgs e)
    {
        _splitRatio = 0.5;
        ApplySplitLayout();
    }

    /// <summary>Clicking into a pane makes its tab the active one (address bar follows focus).</summary>
    private void OnContentFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ViewModel.IsSplit || e.NewFocus is not DependencyObject d) return;
        if (TabFrom(d) is { } tab && !ReferenceEquals(tab, ViewModel.ActiveTab) &&
            (ReferenceEquals(tab, ViewModel.SplitLeft) || ReferenceEquals(tab, ViewModel.SplitRight)))
            ViewModel.ActiveTab = tab;
    }

    // ======================================================================================
    // Command palette (Ctrl+K)
    // ======================================================================================

    private List<PaletteItem> _paletteCommands = [];
    private List<Bookmark> _paletteBookmarks = [];
    private CancellationTokenSource? _paletteCts;

    private async void OpenPalette()
    {
        if (_isFullScreen) return;
        _paletteCommands = BuildCommands();
        PalettePopup.HorizontalOffset = Math.Max(0, (RootBorder.ActualWidth - 636) / 2);
        PalettePopup.VerticalOffset = 52;
        PaletteBox.Text = string.Empty;
        PalettePopup.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); }, DispatcherPriority.Input);
        RefreshPalette();
        try { _paletteBookmarks = (await _s.Bookmarks.GetAllAsync()).Where(b => !b.IsFolder && b.Url is not null).ToList(); }
        catch { _paletteBookmarks = []; }
    }

    private void OnPaletteClosed(object? sender, EventArgs e)
    {
        _paletteCts?.Cancel();
        PaletteList.ItemsSource = null;
    }

    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => RefreshPalette();

    private async void RefreshPalette()
    {
        _paletteCts?.Cancel();
        var cts = _paletteCts = new CancellationTokenSource();
        var q = PaletteBox.Text.Trim();
        var results = new List<PaletteItem>();

        IEnumerable<PaletteItem> Ranked(IEnumerable<PaletteItem> items, int bonus) =>
            items.Select(i => i with { Score = q.Length == 0 ? bonus : Math.Max(FuzzyMatcher.Score(q, i.Title), FuzzyMatcher.Score(q, i.Detail) - 100) + (q.Length == 0 ? 0 : bonus) })
                 .Where(i => q.Length == 0 || i.Score > 0);

        var tabs = ViewModel.Tabs.Where(t => !ReferenceEquals(t, ViewModel.ActiveTab)).Select(t =>
        {
            var tab = t;
            return new PaletteItem(t.Title, t.IsNewTabPage ? "Open tab" : $"Open tab · {UrlDisplay.Host(t.Url)}", Icons.NewWindow, "",
                () => ViewModel.ActiveTab = tab);
        });

        if (q.Length == 0)
        {
            results.AddRange(_paletteCommands.Take(12));
        }
        else
        {
            results.AddRange(Ranked(_paletteCommands, 40));
            results.AddRange(Ranked(tabs, 60));
            results.AddRange(Ranked(_paletteBookmarks.Take(2000).Select(b =>
            {
                var url = b.Url!;
                return new PaletteItem(b.Title, UrlDisplay.Short(url), Icons.Star, "", () => ViewModel.GoTo(url));
            }), 0));
            results = results.OrderByDescending(r => r.Score).Take(12).ToList();

            var parsed = AddressParser.Parse(q, _s.SearchEngine);
            if (parsed is not null)
            {
                var target = parsed.Uri.AbsoluteUri;
                results.Add(parsed.IsSearch
                    ? new PaletteItem($"Search for “{q}”", _s.SearchEngine.Name, Icons.Search, "", () => ViewModel.GoTo(target))
                    : new PaletteItem($"Go to {UrlDisplay.Short(target)}", "Address", Icons.Globe, "", () => ViewModel.GoTo(target)));
            }
        }
        ShowPaletteResults(results);

        if (q.Length < 2 || IsPrivate) return;
        try
        {
            await Task.Delay(120, cts.Token);
            var hist = await _s.History.SuggestAsync(q, 4);
            if (cts.IsCancellationRequested) return;
            var extra = hist.Where(h => results.All(r => r.Detail != UrlDisplay.Short(h.Url))).Select(h =>
            {
                var url = h.Url;
                return new PaletteItem(string.IsNullOrWhiteSpace(h.Title) ? UrlDisplay.Short(url) : h.Title, UrlDisplay.Short(url), Icons.History, "", () => ViewModel.GoTo(url));
            }).ToList();
            if (extra.Count == 0) return;
            results.InsertRange(Math.Max(0, results.Count - 1), extra);
            ShowPaletteResults(results);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _s.Paths.LogError("Palette history", ex); }
    }

    private void ShowPaletteResults(List<PaletteItem> results)
    {
        PaletteList.ItemsSource = results;
        if (results.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void OnPaletteKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                PaletteList.SelectedIndex = Math.Min(PaletteList.Items.Count - 1, PaletteList.SelectedIndex + 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                RunPaletteItem(PaletteList.SelectedItem as PaletteItem);
                break;
            case Key.Escape:
                e.Handled = true;
                PalettePopup.IsOpen = false;
                break;
        }
    }

    private void OnPaletteClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteItem item) RunPaletteItem(item);
    }

    private void RunPaletteItem(PaletteItem? item)
    {
        PalettePopup.IsOpen = false;
        if (item is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            try { item.Run(); }
            catch (Exception ex) { _s.Paths.LogError("Palette command", ex); }
        }, DispatcherPriority.Input);
    }

    private List<PaletteItem> BuildCommands()
    {
        var vm = ViewModel;
        var tab = vm.ActiveTab;
        var web = tab is { IsNewTabPage: false, IsCreated: true };
        var list = new List<PaletteItem>
        {
            new("New tab", "Tabs", Icons.Add, "Ctrl+T", vm.NewTab),
            new("New window", "Windows", Icons.NewWindow, "Ctrl+N", vm.NewWindow),
            new("New private window", "Windows", Icons.Private, "Ctrl+Shift+N", vm.NewPrivateWindow),
            new("Reopen closed tab", "Tabs", Icons.Retry, "Ctrl+Shift+T", vm.ReopenClosedTab),
            new("Bookmarks", "Sidebar", Icons.Bookmarks, "Ctrl+Shift+O", () => OpenPanel(SidebarPanel.Bookmarks)),
            new("History", "Sidebar", Icons.History, "Ctrl+H", () => OpenPanel(SidebarPanel.History)),
            new("Downloads", "Sidebar", Icons.Download, "Ctrl+J", () => OpenPanel(SidebarPanel.Downloads)),
            new("Reading list", "Sidebar", Icons.ReadingList, "", () => OpenPanel(SidebarPanel.ReadingList)),
            new("Settings", "RELAY", Icons.Settings, "", () => App.Current.ShowSettings()),
            new(vm.IsSplit ? "Close split view" : "Split view with a new tab", "Tabs", "\uE89F", "", () => { if (vm.IsSplit) vm.CloseSplit(); else vm.SplitWithNewTab(); }),
            new(vm.VerticalTabs ? "Use horizontal tabs" : "Use vertical tabs", "Tabs", "\uE8A4", "", ToggleVerticalTabs),
            new("Toggle sidebar", "Sidebar", Icons.Sidebar, "Ctrl+Shift+B", ToggleSidebar),
            new("Clear browsing data", "Privacy", Icons.Clear, "", () => OnClearData(this, new RoutedEventArgs())),
            new("Ad blocking settings", "Privacy", "\uEA18", "", () => App.Current.ShowSettings("blocking")),
            new(App.Current.Theme.IsDark ? "Switch to light theme" : "Switch to dark theme", "Appearance", "\uE793", "",
                () => _s.Settings.Update(s => s.Theme = App.Current.Theme.IsDark ? Relay.Core.Settings.ThemeMode.Light : Relay.Core.Settings.ThemeMode.Dark)),
            new("Put background tabs to sleep", "Performance", "\uE708", "", () => { foreach (var t in vm.Tabs) _ = t.SleepNowAsync(); }),
            new("Unload background tabs to free memory", "Performance", "\uE74D", "", () => { foreach (var t in vm.Tabs) t.Discard(); }),
            new("Task manager", "Performance", "\uE9D9", "Shift+Esc", () => App.Current.ShowTaskManager()),
            new("About RELAY", "RELAY", Icons.Info, "", () => App.Current.ShowSettings("about")),
        };
        if (tab is not null)
        {
            list.Insert(1, new("Close tab", "Tabs", Icons.Close, "Ctrl+W", () => vm.CloseTab(tab)));
            list.Add(new("Duplicate tab", "Tabs", Icons.Duplicate, "", () => vm.DuplicateTab(tab)));
            list.Add(new("Add tab to new group", "Tabs", "\uE8F4", "", () => NewGroupFor(tab)));
            list.Add(new("Move tab to a new window", "Windows", Icons.NewWindow, "", () => MoveTabToNewWindow(tab)));
        }
        if (web)
        {
            list.Add(new("Reload", "Page", Icons.Refresh, "Ctrl+R", vm.Reload));
            list.Add(new("Bookmark this page", "Page", Icons.Star, "Ctrl+D", () => OnStarClick(this, new RoutedEventArgs())));
            list.Add(new("Add to reading list", "Page", Icons.ReadingList, "", () => _ = vm.AddToReadingListAsync()));
            list.Add(new("Find on page", "Page", Icons.Find, "Ctrl+F", () => _ = tab!.FindAsync()));
            list.Add(new("Print", "Page", Icons.Print, "Ctrl+P", () => _ = tab!.PrintAsync()));
            list.Add(new("Zoom in", "Page", Icons.ZoomIn, "Ctrl++", vm.ZoomIn));
            list.Add(new("Zoom out", "Page", Icons.ZoomOut, "Ctrl+−", vm.ZoomOut));
            list.Add(new("Reset zoom", "Page", Icons.ZoomOut, "Ctrl+0", vm.ZoomReset));
            list.Add(new("Full screen", "Page", Icons.FullScreen, "F11", ToggleFullScreen));
            list.Add(new("Developer tools", "Page", Icons.Code, "F12", () => tab!.OpenDevTools()));
            list.Add(new("Copy page address", "Page", Icons.Link, "", () => TrySetClipboard(tab!.Url)));
            var site = BlockerService.SiteKey(tab!.Host);
            if (site.Length > 0)
            {
                var allowed = _s.Blocker.IsSiteAllowed(tab.Host);
                list.Add(new(allowed ? $"Turn ad blocking back on for {site}" : $"Turn off ad blocking for {site}", "Privacy", "\uEA18", "", () =>
                {
                    _s.Blocker.SetSiteAllowed(tab.Host, !allowed);
                    tab.UpdateBlockingFilter();
                    tab.Reload();
                }));
            }
        }
        AddExtraCommands(list, tab, web);
        return list;
    }

    /// <summary>Hook for page tools (reader mode, screenshots, …) defined further down.</summary>
    partial void AddExtraCommands(List<PaletteItem> list, BrowserTab? tab, bool web);

    private void MoveTabToNewWindow(BrowserTab tab)
    {
        if (tab.IsNewTabPage) { ViewModel.CloseTab(tab); App.Current.WindowManager?.Open(IsPrivate); return; }
        var url = tab.Url;
        ViewModel.CloseTab(tab);
        App.Current.WindowManager?.Open(IsPrivate, urls: [url]);
    }

    // ======================================================================================
    // Closing
    // ======================================================================================

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        var manager = App.Current.WindowManager;
        if (!_closeReady && !IsPrivate && manager?.IsLastNormalWindow(this) == true && _s.Settings.Current.ClearDataOnExit)
        {
            // "Clear browsing data when RELAY closes": needs a live profile, so do it before the window goes away.
            e.Cancel = true;
            _closeReady = true;
            try { await ClearDataDialog.ClearEverythingAsync(_s); }
            catch (Exception ex) { _s.Paths.LogError("Clear on exit", ex); }
            await Dispatcher.InvokeAsync(Close);
            return;
        }

        SaveBounds();
        manager?.OnClosing(this);
        ViewModel.Omnibox.Close();
        foreach (var p in new[] { BookmarkPopup, DownloadPopup, SiteInfoPopup, ShieldPopup, ToastPopup, QrPopup, PalettePopup, TabPreviewPopup }) p.IsOpen = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Current.Theme.ThemeChanged -= OnThemeChanged;
        _downloadPopupTimer.Stop();
        ViewModel.DisposeAll();
        base.OnClosed(e);
    }
}
