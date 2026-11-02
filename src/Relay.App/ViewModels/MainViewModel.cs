using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Relay.Browser;
using Relay.Data;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.App.ViewModels;

/// <summary>State and commands for one browser window.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly BrowserServices _s;
    private readonly Stack<(string Url, int Index)> _closed = new();

    public MainViewModel(BrowserServices services, bool isPrivate)
    {
        _s = services;
        IsPrivate = isPrivate;
        Omnibox = new OmniboxViewModel(services, isPrivate);
        Sidebar = new SidebarViewModel(services);
        _s.Downloads.ActiveCountChanged += (_, _) => OnPropertyChanged(nameof(ActiveDownloads));
        _s.Bookmarks.Changed += async (_, _) => await RefreshBookmarkStateAsync();
        _s.Passwords.Changed += async (_, _) => await RefreshLoginsAsync();
        _s.Settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowHomeButton));
            OnPropertyChanged(nameof(SidebarEnabled));
            OnPropertyChanged(nameof(ShowTabFavicons));
            OnPropertyChanged(nameof(VerticalTabs));
            OnPropertyChanged(nameof(VerticalTabsCollapsed));
            OnPropertyChanged(nameof(VerticalTabsNarrow));
            OnPropertyChanged(nameof(HorizontalTabs));
            RebuildStrip();
        };
        Tabs.CollectionChanged += (_, _) => RebuildStrip();
    }

    public BrowserServices Services => _s;
    public bool IsPrivate { get; }
    public OmniboxViewModel Omnibox { get; }
    public SidebarViewModel Sidebar { get; }

    /// <summary>Tabs in strip order.</summary>
    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    /// <summary>Tabs in creation order — the content host never reorders, so WebViews are never re-parented.</summary>
    public ObservableCollection<BrowserTab> HostedTabs { get; } = [];

    [ObservableProperty] private BrowserTab? _activeTab;
    [ObservableProperty] private bool _isBookmarked;
    [ObservableProperty] private bool _isFullScreen;

    public int ActiveDownloads => _s.Downloads.ActiveCount;
    public bool ShowHomeButton => _s.Settings.Current.ShowHomeButton;
    public bool SidebarEnabled => _s.Settings.Current.SidebarEnabled;
    public bool ShowTabFavicons => _s.Settings.Current.ShowTabFavicons;
    public bool CanReopenClosed => _closed.Count > 0;
    public bool VerticalTabs => _s.Settings.Current.VerticalTabs;
    public bool HorizontalTabs => !VerticalTabs;
    public bool VerticalTabsCollapsed => _s.Settings.Current.VerticalTabsCollapsed;
    /// <summary>The vertical tab list shows icons only (collapsed and not expanded by hovering).</summary>
    public bool VerticalTabsNarrow => VerticalTabsCollapsed && !SidebarHoverExpanded;
    [ObservableProperty] private bool _sidebarHoverExpanded;
    partial void OnSidebarHoverExpandedChanged(bool value) => OnPropertyChanged(nameof(VerticalTabsNarrow));
    /// <summary>Corner radius of the page, in DIPs (the floating page card of the vertical layout).</summary>
    [ObservableProperty] private double _pageCornerRadius;
    /// <summary>Pinned tabs, shown as tiles at the top of the vertical tab list.</summary>
    public ObservableCollection<BrowserTab> PinnedTabs { get; } = [];

    /// <summary>Saved RELAY logins for the active page's origin (never in private windows).</summary>
    public ObservableCollection<SavedLogin> Logins { get; } = [];
    [ObservableProperty] private bool _hasSavedLogins;

    /// <summary>Asks the window to offer "Save password?" for a submitted login that isn't saved yet.</summary>
    public event EventHandler<Relay.Core.Passwords.ImportedLogin>? SaveLoginOffered;

    private async Task OfferToSaveLoginAsync(Relay.Core.Passwords.ImportedLogin login)
    {
        if (IsPrivate) return;
        try
        {
            foreach (var saved in await _s.Passwords.ForOriginAsync(login.Origin))
                if (saved.Username == login.Username && await _s.Passwords.GetPasswordAsync(saved.Id) == login.Password) return;
        }
        catch { return; }
        SaveLoginOffered?.Invoke(this, login);
    }

    public async Task RefreshLoginsAsync()
    {
        var tab = ActiveTab;
        var origin = IsPrivate || tab is null || tab.IsReaderPage ? null : Relay.Core.Passwords.PasswordOrigin.From(tab.Url);
        List<SavedLogin> list = [];
        try { if (origin is not null) list = await _s.Passwords.ForOriginAsync(origin); }
        catch (Exception ex) { _s.Paths.LogError("Saved logins", ex); }
        if (!ReferenceEquals(tab, ActiveTab)) return;
        Logins.Clear();
        foreach (var l in list) Logins.Add(l);
        HasSavedLogins = Logins.Count > 0;
    }

    public void TogglePin(BrowserTab tab)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;
        var pinnedCount = Tabs.Count(t => t.IsPinned);
        if (tab.IsPinned)
        {
            tab.IsPinned = false;
            Tabs.Move(from, Math.Max(0, pinnedCount - 1));
        }
        else
        {
            tab.Group = null;
            tab.IsPinned = true;
            Tabs.Move(from, pinnedCount);
        }
        CleanupGroups();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the tab strip shows: group headers followed by their tabs (collapsed groups hide their tabs).</summary>
    public ObservableCollection<object> StripItems { get; } = [];
    public ObservableCollection<TabGroup> Groups { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSplit))] private BrowserTab? _splitLeft;
    [ObservableProperty] private BrowserTab? _splitRight;
    public bool IsSplit => SplitLeft is not null && SplitRight is not null;
    public event EventHandler? SplitChanged;

    public event EventHandler? CloseWindowRequested;
    public event EventHandler? FocusAddressRequested;
    public event EventHandler<bool>? NewWindowRequested;
    public event EventHandler<bool>? FullScreenRequested;
    public event EventHandler? TabsChanged;

    // ---- Tabs ------------------------------------------------------------------------

    public BrowserTab AddTab(string? url = null, bool activate = true, int? index = null, bool popupTarget = false, bool lazy = false)
    {
        var tab = new BrowserTab(_s, IsPrivate, url, popupTarget);
        tab.NewWindowRequested += OnTabNewWindowRequested;
        tab.LoginSubmitted += async (_, login) => await OfferToSaveLoginAsync(login);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.FullScreenChanged += (_, full) => { if (ReferenceEquals(tab, ActiveTab)) FullScreenRequested?.Invoke(this, full); };
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserTab.Zoom) && ReferenceEquals(tab, ActiveTab)) NotifyZoomChanged();
            if (e.PropertyName == nameof(BrowserTab.Group)) { RebuildStrip(); TabsChanged?.Invoke(this, EventArgs.Empty); }
            if (e.PropertyName is nameof(BrowserTab.Url) or nameof(BrowserTab.Title))
            {
                TabsChanged?.Invoke(this, EventArgs.Empty);
                if (ReferenceEquals(tab, ActiveTab) && e.PropertyName == nameof(BrowserTab.Url)) { _ = RefreshBookmarkStateAsync(); _ = RefreshLoginsAsync(); }
            }
        };

        var i = Math.Clamp(index ?? Tabs.Count, 0, Tabs.Count);
        Tabs.Insert(i, tab);
        HostedTabs.Add(tab);
        if (activate || ActiveTab is null) ActiveTab = tab;
        else if (!popupTarget && !lazy) _ = tab.EnsureCreatedAsync(); // background tab: start loading now
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    private async void OnTabNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (sender is not BrowserTab opener) return;
        var deferral = e.GetDeferral();
        try
        {
            // Popups and target=_blank links open as a tab right next to the page that opened them.
            // WebView2 doesn't report whether Ctrl was held, so new tabs always come to the front.
            var index = Tabs.IndexOf(opener) + 1;
            var tab = AddTab(null, activate: true, index: index, popupTarget: true);
            tab.Group = opener.Group; // links opened from a grouped tab stay in its group
            await tab.EnsureCreatedAsync();
            if (tab.Core is not null) e.NewWindow = tab.Core;
            else e.Handled = true;
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("New window", ex);
            e.Handled = true;
        }
        finally { deferral.Complete(); }
    }

    partial void OnActiveTabChanged(BrowserTab? oldValue, BrowserTab? newValue)
    {
        var inPair = IsSplit && newValue is not null && (ReferenceEquals(newValue, SplitLeft) || ReferenceEquals(newValue, SplitRight));
        if (IsSplit && !inPair && newValue is not null) CloseSplit();
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            // In split view the other pane stays on screen.
            if (inPair) oldValue.IsSplitVisible = true;
        }
        if (newValue is null) return;
        newValue.IsSplitVisible = false;
        newValue.IsActive = true;
        if (newValue.Group is { IsCollapsed: true } g) { g.IsCollapsed = false; RebuildStrip(); }
        _ = newValue.EnsureCreatedAsync();
        _ = RefreshLoginsAsync();
        _ = RefreshBookmarkStateAsync();
        if (newValue.IsNewTabPage) _ = newValue.PushNewTabStateAsync();
        NotifyZoomChanged();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void NewTab()
    {
        AddTab();
        FocusAddressRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void CloseTab(BrowserTab? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (ReferenceEquals(tab, SplitLeft) || ReferenceEquals(tab, SplitRight)) CloseSplit();
        if (!tab.IsNewTabPage && !string.IsNullOrEmpty(tab.Url)) _closed.Push((tab.Url, index));
        OnPropertyChanged(nameof(CanReopenClosed));

        if (ReferenceEquals(tab, ActiveTab))
        {
            // Activate the tab to the right, like most browsers, otherwise the one to the left.
            ActiveTab = Tabs.Count > 1 ? Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1] : null;
        }
        Tabs.Remove(tab);
        HostedTabs.Remove(tab);
        var group = tab.Group;
        tab.Group = null;
        if (group is not null && !Tabs.Any(t => ReferenceEquals(t.Group, group))) Groups.Remove(group);
        tab.Dispose();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);

        if (Tabs.Count == 0) CloseWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ReopenClosedTab()
    {
        if (_closed.Count == 0) return;
        var (url, index) = _closed.Pop();
        AddTab(url, activate: true, index: index);
        OnPropertyChanged(nameof(CanReopenClosed));
    }

    [RelayCommand] public void NextTab() => Cycle(1);
    [RelayCommand] public void PreviousTab() => Cycle(-1);

    private void Cycle(int delta)
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var i = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    public void SelectTabNumber(int n)
    {
        if (Tabs.Count == 0) return;
        ActiveTab = n >= 9 ? Tabs[^1] : Tabs[Math.Min(n - 1, Tabs.Count - 1)];
    }

    public void MoveTab(BrowserTab tab, int newIndex)
    {
        var old = Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (old < 0 || old == newIndex) return;
        Tabs.Move(old, newIndex);
        // Group membership follows the tab's new neighbours.
        var left = newIndex > 0 ? Tabs[newIndex - 1] : null;
        var right = newIndex < Tabs.Count - 1 ? Tabs[newIndex + 1] : null;
        if (left?.Group is not null && ReferenceEquals(left.Group, right?.Group)) tab.Group = left.Group;
        else if (tab.Group is not null && !ReferenceEquals(left?.Group, tab.Group) && !ReferenceEquals(right?.Group, tab.Group)) tab.Group = null;
        CleanupGroups();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves a tab to the slot of a strip item (a tab or a group header) — used by drag and drop.</summary>
    public void MoveTabTo(BrowserTab tab, object target)
    {
        var index = target switch
        {
            BrowserTab t => Tabs.IndexOf(t),
            TabGroup g => Tabs.ToList().FindIndex(t => ReferenceEquals(t.Group, g)),
            _ => -1,
        };
        if (index >= 0) MoveTab(tab, index);
    }

    // ---- Tab groups ------------------------------------------------------------------

    public TabGroup CreateGroup(BrowserTab tab, string name, string color)
    {
        var g = new TabGroup(name, color);
        Groups.Add(g);
        tab.Group = g;
        CleanupGroups();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return g;
    }

    public void AddToGroup(BrowserTab tab, TabGroup g)
    {
        var last = Tabs.ToList().FindLastIndex(t => ReferenceEquals(t.Group, g) && !ReferenceEquals(t, tab));
        tab.Group = g;
        if (last >= 0)
        {
            var from = Tabs.IndexOf(tab);
            var to = from > last ? last + 1 : last;
            if (from != to) Tabs.Move(from, to);
        }
        CleanupGroups();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFromGroup(BrowserTab tab)
    {
        if (tab.Group is not { } g) return;
        var last = Tabs.ToList().FindLastIndex(t => ReferenceEquals(t.Group, g));
        tab.Group = null;
        var from = Tabs.IndexOf(tab);
        if (last > from) Tabs.Move(from, last);
        CleanupGroups();
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Ungroup(TabGroup g)
    {
        foreach (var t in Tabs.Where(t => ReferenceEquals(t.Group, g)).ToList()) t.Group = null;
        Groups.Remove(g);
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseGroup(TabGroup g)
    {
        foreach (var t in Tabs.Where(t => ReferenceEquals(t.Group, g)).ToList()) CloseTab(t);
        Groups.Remove(g);
    }

    public void ToggleCollapse(TabGroup g)
    {
        if (!g.IsCollapsed && ActiveTab is { } active && ReferenceEquals(active.Group, g))
        {
            // Move focus out of the group before hiding it.
            var outside = Tabs.FirstOrDefault(t => !ReferenceEquals(t.Group, g));
            if (outside is null) return;
            ActiveTab = outside;
        }
        g.IsCollapsed = !g.IsCollapsed;
        RebuildStrip();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupGroups()
    {
        foreach (var g in Groups.Where(g => !Tabs.Any(t => ReferenceEquals(t.Group, g))).ToList()) Groups.Remove(g);
    }

    public void RebuildStrip()
    {
        var vertical = VerticalTabs;
        var pinned = Tabs.Where(t => t.IsPinned).ToList();
        for (var i = PinnedTabs.Count - 1; i >= 0; i--)
            if (!pinned.Contains(PinnedTabs[i])) PinnedTabs.RemoveAt(i);
        for (var i = 0; i < pinned.Count; i++)
        {
            var cur = PinnedTabs.IndexOf(pinned[i]);
            if (cur < 0) PinnedTabs.Insert(i, pinned[i]);
            else if (cur != i) PinnedTabs.Move(cur, i);
        }

        var desired = new List<object>();
        TabGroup? prev = null;
        foreach (var t in Tabs)
        {
            if (vertical && t.IsPinned) { prev = null; continue; }
            if (t.Group is { } g && !ReferenceEquals(g, prev)) desired.Add(g);
            prev = t.Group;
            if (t.Group is { IsCollapsed: true }) continue;
            desired.Add(t);
        }
        foreach (var g in Groups) g.Count = Tabs.Count(t => ReferenceEquals(t.Group, g));

        for (var i = StripItems.Count - 1; i >= 0; i--)
            if (!desired.Contains(StripItems[i])) StripItems.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var cur = StripItems.IndexOf(desired[i]);
            if (cur < 0) StripItems.Insert(i, desired[i]);
            else if (cur != i) StripItems.Move(cur, i);
        }
    }

    // ---- Split view ------------------------------------------------------------------

    /// <summary>Shows <paramref name="other"/> next to the current tab.</summary>
    public void OpenSplit(BrowserTab other)
    {
        if (ActiveTab is not { } left || ReferenceEquals(left, other)) return;
        if (IsSplit) CloseSplit();
        SplitLeft = left;
        SplitRight = other;
        left.SplitSide = 1;
        other.SplitSide = 2;
        other.IsSplitVisible = true;
        _ = other.EnsureCreatedAsync();
        OnPropertyChanged(nameof(IsSplit));
        SplitChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens a new tab beside the current one.</summary>
    public void SplitWithNewTab()
    {
        if (ActiveTab is null) return;
        var t = AddTab(null, activate: false, index: Tabs.IndexOf(ActiveTab) + 1);
        OpenSplit(t);
        ActiveTab = t;
    }

    public void CloseSplit()
    {
        foreach (var t in new[] { SplitLeft, SplitRight })
        {
            if (t is null) continue;
            t.SplitSide = 0;
            t.IsSplitVisible = false;
        }
        SplitLeft = null;
        SplitRight = null;
        OnPropertyChanged(nameof(IsSplit));
        SplitChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SwapSplit()
    {
        if (!IsSplit) return;
        (SplitLeft, SplitRight) = (SplitRight, SplitLeft);
        SplitLeft!.SplitSide = 1;
        SplitRight!.SplitSide = 2;
        SplitChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DuplicateTab(BrowserTab tab) =>
        AddTab(tab.IsNewTabPage ? null : tab.Url, activate: true, index: Tabs.IndexOf(tab) + 1);

    public void CloseOtherTabs(BrowserTab keep)
    {
        foreach (var t in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) CloseTab(t);
    }

    public void CloseTabsToRight(BrowserTab tab)
    {
        var i = Tabs.IndexOf(tab);
        foreach (var t in Tabs.Skip(i + 1).ToList()) CloseTab(t);
    }

    // ---- Navigation ------------------------------------------------------------------

    /// <summary>Navigates the active tab to what was typed (URL or search).</summary>
    public void Go(string text)
    {
        var result = AddressParser.Parse(text, _s.SearchEngine);
        if (result is null) return;
        GoTo(result.Uri.AbsoluteUri);
    }

    public void GoTo(string url)
    {
        if (ActiveTab is null) AddTab(url);
        else ActiveTab.Navigate(url);
        ActiveTab?.WebView?.Focus();
    }

    public void OpenInNewTab(string url, bool activate = true)
    {
        var index = ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1;
        AddTab(url, activate, index);
    }

    [RelayCommand] public void Back() => ActiveTab?.GoBack();
    [RelayCommand] public void Forward() => ActiveTab?.GoForward();

    [RelayCommand]
    public void ReloadOrStop()
    {
        if (ActiveTab is null) return;
        if (ActiveTab.IsLoading) ActiveTab.Stop(); else ActiveTab.Reload();
    }

    [RelayCommand] public void Reload() => ActiveTab?.Reload();
    [RelayCommand] public void Home() => GoTo(_s.Settings.Current.Homepage);

    [RelayCommand] public void NewWindow() => NewWindowRequested?.Invoke(this, false);
    [RelayCommand] public void NewPrivateWindow() => NewWindowRequested?.Invoke(this, true);

    // ---- Bookmarks / reading list ----------------------------------------------------

    public async Task RefreshBookmarkStateAsync()
    {
        var url = ActiveTab?.Url;
        if (string.IsNullOrEmpty(url) || ActiveTab!.IsNewTabPage) { IsBookmarked = false; return; }
        try { IsBookmarked = await _s.Bookmarks.FindByUrlAsync(url) is not null; }
        catch { IsBookmarked = false; }
    }

    /// <summary>Bookmarks the current page if needed and returns the bookmark (for the edit popup).</summary>
    public async Task<Bookmark?> EnsureBookmarkAsync()
    {
        var tab = ActiveTab;
        if (tab is null || tab.IsNewTabPage || string.IsNullOrEmpty(tab.Url)) return null;
        var existing = await _s.Bookmarks.FindByUrlAsync(tab.Url);
        if (existing is not null) return existing;
        var b = await _s.Bookmarks.AddAsync(string.IsNullOrWhiteSpace(tab.Title) ? tab.Host : tab.Title, tab.Url, null);
        IsBookmarked = true;
        return b;
    }

    public async Task AddToReadingListAsync(BrowserTab? tab = null)
    {
        tab ??= ActiveTab;
        if (tab is null || tab.IsNewTabPage || !tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        await _s.ReadingList.AddAsync(string.IsNullOrWhiteSpace(tab.Title) ? tab.Host : tab.Title, tab.Url);
    }

    // ---- Zoom ------------------------------------------------------------------------

    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    public void ZoomIn() => StepZoom(+1);
    public void ZoomOut() => StepZoom(-1);
    public void ZoomReset() { ActiveTab?.SetZoom(_s.Settings.Current.DefaultZoom); NotifyZoomChanged(); }

    private void StepZoom(int dir)
    {
        if (ActiveTab is null) return;
        var z = ActiveTab.Zoom;
        var next = dir > 0 ? ZoomSteps.FirstOrDefault(s => s > z + 0.001, 5.0) : ZoomSteps.LastOrDefault(s => s < z - 0.001, 0.25);
        ActiveTab.SetZoom(next);
        NotifyZoomChanged();
    }

    public string ZoomText => $"{Math.Round((ActiveTab?.Zoom ?? 1) * 100)}%";

    public bool IsZoomed => ActiveTab is not null && Math.Abs(ActiveTab.Zoom - 1.0) > 0.001;

    public void NotifyZoomChanged()
    {
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(IsZoomed));
    }

    // ---- Session ---------------------------------------------------------------------

    public IEnumerable<SessionTab> Snapshot(int windowIndex) =>
        Tabs.Select((t, i) => new SessionTab
        {
            WindowIndex = windowIndex,
            Position = i,
            Url = t.IsNewTabPage || string.IsNullOrEmpty(t.Url) ? UrlDisplay.NewTabUrl : t.IsReaderPage ? t.ReaderOriginalUrl ?? UrlDisplay.NewTabUrl : t.Url,
            Title = t.Title,
            IsActive = ReferenceEquals(t, ActiveTab),
            GroupName = t.Group?.Name,
            GroupColor = t.Group?.ColorName,
            GroupCollapsed = t.Group?.IsCollapsed ?? false,
            IsPinned = t.IsPinned,
        });

    /// <summary>Recreates groups when restoring a session (consecutive tabs with the same group belong together).</summary>
    public void RestoreGroups(IReadOnlyList<(BrowserTab Tab, SessionTab Saved)> tabs)
    {
        TabGroup? current = null;
        string? key = null;
        foreach (var (tab, saved) in tabs)
        {
            tab.IsPinned = saved.IsPinned;
            if (saved.GroupName is null && saved.GroupColor is null) { current = null; key = null; continue; }
            var k = saved.GroupName + "|" + saved.GroupColor;
            if (current is null || k != key)
            {
                current = new TabGroup(saved.GroupName ?? string.Empty, saved.GroupColor ?? "Blue") { IsCollapsed = saved.GroupCollapsed };
                Groups.Add(current);
                key = k;
            }
            tab.Group = current;
        }
        if (ActiveTab?.Group is { IsCollapsed: true } g) g.IsCollapsed = false;
        RebuildStrip();
    }

    public void DisposeAll()
    {
        CloseSplit();
        foreach (var t in Tabs.ToList()) t.Dispose();
        Tabs.Clear();
        HostedTabs.Clear();
    }
}
