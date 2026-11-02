using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Relay.App.Themes;
using Relay.Browser;
using Relay.Core.Models;
using Relay.Core.Search;
using Relay.Core.Settings;
using ThemeMode = Relay.Core.Settings.ThemeMode;

namespace Relay.App.ViewModels;

public sealed record EngineOption(string Id, string Name);
public sealed record AccentOption(string Name, SolidColorBrush Brush);
public sealed record ZoomOption(double Value, string Text);

public sealed partial class SitePermissionRow : ObservableObject
{
    public required SitePermission Permission { get; init; }
    public string Origin => Permission.Origin;
    public string Kind => Permission.Kind;
    public string Decision => Permission.Decision == PermissionDecision.Allow ? "Allowed" : "Blocked";
}

public sealed record CookieSiteRow(string Domain, int Count);

public sealed partial class FilterListRow : ObservableObject
{
    private readonly Action<FilterListRow> _changed;
    public FilterListRow(FilterList list, bool enabled, string updated, Action<FilterListRow> changed)
    {
        List = list;
        _isEnabled = enabled;
        _updated = updated;
        _changed = changed;
    }
    public FilterList List { get; }
    public string Name => List.Name;
    public string Description => List.Description;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _updated;
    partial void OnIsEnabledChanged(bool value) => _changed(this);
}

/// <summary>Two-way view of <see cref="RelaySettings"/>; every change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BrowserServices _s;
    private RelaySettings S => _s.Settings.Current;

    public SettingsViewModel(BrowserServices services)
    {
        _s = services;
        _homepageText = S.Homepage;
        _customEngineText = S.CustomSearchTemplate ?? string.Empty;
        Engines = [.. SearchEngines.BuiltIn.Select(e => new EngineOption(e.Id, e.Name)), new EngineOption(SearchEngines.CustomId, "Custom…")];
        Accents = [.. ThemeManager.Accents.Select(a => new AccentOption(a.Key, new SolidColorBrush(a.Value.Light)))];
        FontChoices = ["Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Arial", "Verdana", "Tahoma"];
    }

    private void Set<T>(Action<RelaySettings> apply, [CallerMemberName] string? name = null)
    {
        _s.Settings.Update(apply);
        OnPropertyChanged(name);
    }

    // ---- General ----------------------------------------------------------------------
    public StartupBehavior Startup { get => S.Startup; set => Set<StartupBehavior>(s => s.Startup = value); }
    public bool ShowHomeButton { get => S.ShowHomeButton; set => Set<bool>(s => s.ShowHomeButton = value); }
    public bool SearchSuggestions { get => S.SearchSuggestions; set => Set<bool>(s => s.SearchSuggestions = value); }

    public IReadOnlyList<EngineOption> Engines { get; }

    public EngineOption? Engine
    {
        get => Engines.FirstOrDefault(e => e.Id == S.SearchEngineId) ?? Engines[0];
        set
        {
            if (value is null) return;
            Set<string>(s => { s.SearchEngineId = value.Id; s.SearchEngineChosen = true; });
            OnPropertyChanged(nameof(IsCustomEngine));
        }
    }

    public bool IsCustomEngine => S.SearchEngineId == SearchEngines.CustomId;

    [ObservableProperty] private string _customEngineText;
    [ObservableProperty] private string? _customEngineError;

    public void CommitCustomEngine()
    {
        var t = CustomEngineText.Trim();
        if (!SearchEngine.IsValidTemplate(t))
        {
            CustomEngineError = "Use a full https:// address with %s where the search words go.";
            return;
        }
        CustomEngineError = null;
        _s.Settings.Update(s => s.CustomSearchTemplate = t);
    }

    [ObservableProperty] private string _homepageText;
    [ObservableProperty] private string? _homepageError;

    public void CommitHomepage()
    {
        var r = Relay.Core.Navigation.AddressParser.Parse(HomepageText, _s.SearchEngine);
        if (r is null || r.IsSearch)
        {
            HomepageError = "Enter a web address, like example.com.";
            return;
        }
        HomepageError = null;
        HomepageText = r.Uri.AbsoluteUri;
        _s.Settings.Update(s => s.Homepage = r.Uri.AbsoluteUri);
    }

    // ---- Performance ------------------------------------------------------------------
    public sealed record SleepOption(int Minutes, string Text);
    public IReadOnlyList<SleepOption> SleepOptions { get; } =
        [new(5, "After 5 minutes"), new(15, "After 15 minutes"), new(30, "After 30 minutes"), new(60, "After 1 hour"), new(0, "Never")];
    public SleepOption? SleepAfter
    {
        get => SleepOptions.FirstOrDefault(o => o.Minutes == S.SleepTabsAfterMinutes) ?? SleepOptions[1];
        set { if (value is not null) Set<int>(s => s.SleepTabsAfterMinutes = value.Minutes); }
    }
    public bool TabHoverPreviews { get => S.TabHoverPreviews; set => Set<bool>(s => s.TabHoverPreviews = value); }

    // ---- Appearance -------------------------------------------------------------------
    public ThemeMode Theme { get => S.Theme; set => Set<ThemeMode>(s => s.Theme = value); }
    public IReadOnlyList<AccentOption> Accents { get; }
    public AccentOption? Accent
    {
        get => Accents.FirstOrDefault(a => a.Name == S.Accent);
        set { if (value is not null) Set<string>(s => s.Accent = value.Name); }
    }
    public TabDensity TabDensity { get => S.TabDensity; set => Set<TabDensity>(s => s.TabDensity = value); }
    public bool ShowTabFavicons { get => S.ShowTabFavicons; set => Set<bool>(s => s.ShowTabFavicons = value); }
    public bool SidebarEnabled { get => S.SidebarEnabled; set => Set<bool>(s => s.SidebarEnabled = value); }

    public IReadOnlyList<string> FontChoices { get; }
    public string InterfaceFont { get => S.InterfaceFont; set => Set<string>(s => s.InterfaceFont = value); }
    public IReadOnlyList<double> FontSizes { get; } = [12, 12.5, 13, 14, 15];
    public double InterfaceFontSize { get => S.InterfaceFontSize; set => Set<double>(s => s.InterfaceFontSize = value); }

    public IReadOnlyList<ZoomOption> Zooms { get; } =
        [new(0.75, "75%"), new(0.8, "80%"), new(0.9, "90%"), new(1.0, "100%"), new(1.1, "110%"), new(1.25, "125%"), new(1.5, "150%"), new(1.75, "175%"), new(2.0, "200%")];
    public ZoomOption? DefaultZoom
    {
        get => Zooms.FirstOrDefault(z => Math.Abs(z.Value - S.DefaultZoom) < 0.001) ?? Zooms[3];
        set { if (value is not null) Set<double>(s => s.DefaultZoom = value.Value); }
    }

    public bool NewTabShowClock { get => S.NewTabShowClock; set => Set<bool>(s => s.NewTabShowClock = value); }
    public bool NewTabShowRecent { get => S.NewTabShowRecent; set => Set<bool>(s => s.NewTabShowRecent = value); }
    public bool NewTabShowBookmarks { get => S.NewTabShowBookmarks; set => Set<bool>(s => s.NewTabShowBookmarks = value); }
    public string BackgroundSummary => S.NewTabBackground switch
    {
        "custom" => "Your image",
        "none" => "None",
        var p => char.ToUpperInvariant(p[0]) + p[1..],
    };

    public void SetCustomBackground(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp"))
            throw new InvalidDataException("Choose a JPG, PNG, WebP, GIF or BMP image.");
        if (new FileInfo(sourcePath).Length > 15 * 1024 * 1024)
            throw new InvalidDataException("Choose an image smaller than 15 MB.");
        foreach (var old in Directory.GetFiles(_s.Paths.Backgrounds)) { try { File.Delete(old); } catch { } }
        var name = $"background-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        File.Copy(sourcePath, Path.Combine(_s.Paths.Backgrounds, name), overwrite: true);
        _s.Settings.Update(s => { s.NewTabBackground = "custom"; s.NewTabBackgroundImage = name; });
        OnPropertyChanged(nameof(BackgroundSummary));
    }

    public void ClearBackground()
    {
        _s.Settings.Update(s => { s.NewTabBackground = "none"; s.NewTabBackgroundImage = null; });
        OnPropertyChanged(nameof(BackgroundSummary));
    }

    // ---- Privacy ----------------------------------------------------------------------
    public bool ClearDataOnExit { get => S.ClearDataOnExit; set => Set<bool>(s => s.ClearDataOnExit = value); }
    public bool PasswordAutosave { get => S.PasswordAutosave; set => Set<bool>(s => s.PasswordAutosave = value); }
    public bool GeneralAutofill { get => S.GeneralAutofill; set => Set<bool>(s => s.GeneralAutofill = value); }
    public bool PrivateSearchSuggestions { get => S.PrivateSearchSuggestions; set => Set<bool>(s => s.PrivateSearchSuggestions = value); }

    public ObservableCollection<SitePermissionRow> Permissions { get; } = [];
    public ObservableCollection<CookieSiteRow> CookieSites { get; } = [];
    [ObservableProperty] private string _cookieStatus = string.Empty;
    [ObservableProperty] private string _cookieFilter = string.Empty;
    private List<CookieSiteRow> _allCookieSites = [];

    partial void OnCookieFilterChanged(string value) => FilterCookies();

    public async Task LoadPermissionsAsync()
    {
        Permissions.Clear();
        foreach (var p in await _s.Permissions.GetAllAsync()) Permissions.Add(new SitePermissionRow { Permission = p });
    }

    public async Task SetPermissionAsync(SitePermissionRow row, PermissionDecision d)
    {
        await _s.Permissions.SetAsync(row.Origin, row.Kind, d);
        await LoadPermissionsAsync();
    }

    public async Task LoadCookiesAsync()
    {
        var cm = Views.ClearDataDialog.NormalCookieManager;
        if (cm is null)
        {
            _allCookieSites = [];
            CookieStatus = "Open a page in a normal window to manage cookies.";
            FilterCookies();
            return;
        }
        var cookies = await cm.GetCookiesAsync(string.Empty);
        _allCookieSites = cookies.GroupBy(c => c.Domain.TrimStart('.').ToLowerInvariant())
            .Select(g => new CookieSiteRow(g.Key, g.Count()))
            .OrderBy(r => r.Domain).ToList();
        CookieStatus = _allCookieSites.Count == 0 ? "No sites have stored cookies." : $"{cookies.Count} cookies from {_allCookieSites.Count} sites";
        FilterCookies();
    }

    private void FilterCookies()
    {
        CookieSites.Clear();
        foreach (var r in _allCookieSites.Where(r => CookieFilter.Length == 0 || r.Domain.Contains(CookieFilter.Trim(), StringComparison.OrdinalIgnoreCase)).Take(500))
            CookieSites.Add(r);
    }

    public async Task DeleteCookiesAsync(string? domain)
    {
        var cm = Views.ClearDataDialog.NormalCookieManager;
        if (cm is null) return;
        if (domain is null) cm.DeleteAllCookies();
        else
        {
            foreach (var c in await cm.GetCookiesAsync(string.Empty))
                if (c.Domain.TrimStart('.').Equals(domain, StringComparison.OrdinalIgnoreCase)) cm.DeleteCookie(c);
        }
        await LoadCookiesAsync();
    }

    // ---- Ad & tracker blocking ---------------------------------------------------------
    public bool BlockerEnabled
    {
        get => S.BlockerEnabled;
        set { Set<bool>(s => s.BlockerEnabled = value); _ = _s.Blocker.RebuildAsync(); }
    }

    public ObservableCollection<FilterListRow> FilterLists { get; } = [];
    public ObservableCollection<string> AllowedSites { get; } = [];
    [ObservableProperty] private string _blockerStatus = string.Empty;
    [ObservableProperty] private string _blockedTotalText = string.Empty;
    [ObservableProperty] private string _customRulesText = string.Empty;

    public void LoadBlocker()
    {
        FilterLists.Clear();
        foreach (var l in BlockerService.Lists)
            FilterLists.Add(new FilterListRow(l, S.BlockerLists.Contains(l.Id), ListUpdatedText(l.Id), OnFilterListToggled));
        AllowedSites.Clear();
        foreach (var site in S.BlockerAllowedSites.OrderBy(x => x)) AllowedSites.Add(site);
        CustomRulesText = S.BlockerCustomRules;
        RefreshBlockerStatus();
    }

    public void RefreshBlockerStatus()
    {
        BlockerStatus = _s.Blocker.Status;
        BlockedTotalText = $"{_s.Blocker.TotalBlocked:N0} ads and trackers blocked so far";
        foreach (var row in FilterLists) row.Updated = ListUpdatedText(row.List.Id);
    }

    private string ListUpdatedText(string id) =>
        _s.Blocker.ListDate(id) is DateTime d ? $"Updated {d.ToLocalTime():d MMM yyyy, HH:mm}" : "Not downloaded yet";

    private async void OnFilterListToggled(FilterListRow row)
    {
        _s.Settings.Update(s =>
        {
            s.BlockerLists.Remove(row.List.Id);
            if (row.IsEnabled) s.BlockerLists.Add(row.List.Id);
        });
        if (row.IsEnabled && _s.Blocker.ListDate(row.List.Id) is null) await _s.Blocker.UpdateAsync();
        else await _s.Blocker.RebuildAsync();
        RefreshBlockerStatus();
    }

    public async Task UpdateListsAsync()
    {
        BlockerStatus = "Updating filter lists…";
        await _s.Blocker.UpdateAsync();
        RefreshBlockerStatus();
    }

    public async Task SaveCustomRulesAsync()
    {
        var text = CustomRulesText.Replace("\r", "");
        _s.Settings.Update(s => s.BlockerCustomRules = text);
        await _s.Blocker.RebuildAsync();
        RefreshBlockerStatus();
    }

    public void RemoveAllowedSite(string site)
    {
        _s.Blocker.SetSiteAllowed(site, false);
        AllowedSites.Remove(site);
    }

    // ---- Downloads --------------------------------------------------------------------
    public string DownloadDirectory => _s.DownloadDirectory;
    public bool AskWhereToSave { get => S.AskWhereToSave; set => Set<bool>(s => s.AskWhereToSave = value); }
    public bool DownloadNotifications { get => S.DownloadNotifications; set => Set<bool>(s => s.DownloadNotifications = value); }

    public void SetDownloadDirectory(string dir)
    {
        _s.Settings.Update(s => s.DownloadDirectory = dir);
        OnPropertyChanged(nameof(DownloadDirectory));
        foreach (var w in App.Current.WindowManager?.All ?? [])
            foreach (var t in w.ViewModel.Tabs) t.ApplySettings();
    }

    // ---- About ------------------------------------------------------------------------
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string EngineVersion => _s.Environment.Current?.BrowserVersionString ?? WebViewRuntime.GetInstalledVersion() ?? "Unknown";
    public string SdkVersion => typeof(Microsoft.Web.WebView2.Core.CoreWebView2).Assembly.GetName().Version?.ToString() ?? "Unknown";
    public string DotNetVersion => Environment.Version.ToString();
    public string DataFolder => _s.Paths.Root;
    public string StorageMode => _s.Paths.IsPortable ? "Portable (data is stored next to RELAY.exe)" : "Per-user (data is stored in your local app data folder)";

    public string Licenses
    {
        get
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
                return File.Exists(p) ? File.ReadAllText(p) : "THIRD-PARTY-NOTICES.txt is missing from the RELAY folder.";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
