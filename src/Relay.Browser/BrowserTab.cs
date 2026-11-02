using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Relay.Core.Navigation;

namespace Relay.Browser;

public enum SecurityState { None, Secure, NotSecure, Internal, File }

/// <summary>
/// One browser tab: owns its WebView2 control and exposes bindable state.
/// Failures are contained per tab — a crashed renderer shows a recovery panel instead of taking down the app.
/// </summary>
public sealed partial class BrowserTab : ObservableObject, IDisposable
{
    private readonly BrowserServices _s;
    private string? _pendingUrl;
    private Task? _creating;
    private bool _disposed;
    private string _lastFaviconHost = string.Empty;

    public BrowserTab(BrowserServices services, bool isPrivate, string? url, bool isPopupTarget = false)
    {
        _s = services;
        IsPrivate = isPrivate;
        IsPopupTarget = isPopupTarget;
        _pendingUrl = isPopupTarget ? null : string.IsNullOrEmpty(url) ? UrlDisplay.NewTabUrl : url;
        _url = _pendingUrl ?? string.Empty;
        _title = _pendingUrl is null || UrlDisplay.IsNewTab(_pendingUrl) ? "New tab" : UrlDisplay.Host(_pendingUrl);
        _isLoading = !UrlDisplay.IsNewTab(_pendingUrl);
        _s.NewTab.StateChanged += OnNewTabStateChanged;
        _ = Task.Delay(400).ContinueWith(_ => IsNew = false, TaskScheduler.FromCurrentSynchronizationContext());
        if (_pendingUrl is not null && !UrlDisplay.IsNewTab(_pendingUrl)) _ = LoadCachedFaviconAsync(_pendingUrl);
    }

    public bool IsPrivate { get; }
    public bool IsPopupTarget { get; }

    [ObservableProperty] private WebView2? _webView;
    [ObservableProperty] private string _title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddressText), nameof(IsNewTabPage), nameof(Security), nameof(Host), nameof(IsReaderPage))] private string _url;
    [ObservableProperty] private ImageSource? _favicon;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowWebView), nameof(ShowCrashPanel))] private bool _isCrashed;
    [ObservableProperty] private string _crashTitle = string.Empty;
    [ObservableProperty] private string _crashMessage = string.Empty;
    [ObservableProperty] private bool _isPlayingAudio;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowWebView), nameof(ShowCrashPanel))] private bool _isActive;
    /// <summary>Shown in the second pane of split view.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowWebView), nameof(ShowCrashPanel), nameof(IsVisibleTab))] private bool _isSplitVisible;
    [ObservableProperty] private bool _isSleeping;
    /// <summary>The page was unloaded to free memory; it reloads from <see cref="Url"/> when the tab is shown.</summary>
    [ObservableProperty] private bool _isDiscarded;
    [ObservableProperty] private TabGroup? _group;
    /// <summary>0 = not split, 1 = left pane, 2 = right pane.</summary>
    [ObservableProperty] private int _splitSide;
    [ObservableProperty] private ImageSource? _thumbnail;
    /// <summary>True for a moment after the tab is created (drives the entrance animation).</summary>
    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowWebView))] private bool _isObscured;
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private bool _isBlockingActive;

    public string AddressText => IsReaderPage && ReaderOriginalUrl is { } original ? UrlDisplay.ForAddressBar(original) : UrlDisplay.ForAddressBar(Url);
    public bool IsNewTabPage => UrlDisplay.IsNewTab(Url);
    public string Host => UrlDisplay.Host(Url);
    public bool IsVisibleTab => IsActive || IsSplitVisible;
    public bool ShowWebView => IsVisibleTab && !IsCrashed && !IsObscured;
    public bool ShowCrashPanel => IsVisibleTab && IsCrashed;
    public CoreWebView2? Core => WebView?.CoreWebView2;
    public bool IsCreated => Core is not null;

    public SecurityState Security =>
        IsNewTabPage || UrlDisplay.IsInternal(Url) ? SecurityState.Internal :
        Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? SecurityState.Secure :
        Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? SecurityState.NotSecure :
        Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? SecurityState.File : SecurityState.None;

    public event EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? FullScreenChanged;

    /// <summary>Creates the WebView2 lazily (restored background tabs stay cheap until opened).</summary>
    public Task EnsureCreatedAsync() => _creating ??= CreateAsync();

    private async Task CreateAsync()
    {
        if (_disposed) return;
        var env = await _s.Environment.GetAsync();
        if (_disposed) return;

        var wv = new WebView2
        {
            DefaultBackgroundColor = _s.IsDarkTheme ? System.Drawing.Color.FromArgb(255, 32, 33, 36) : System.Drawing.Color.White,
            Focusable = true,
        };
        WebView = wv; // the host adds it to the visual tree; initialization completes once it has a window

        try
        {
            await wv.EnsureCoreWebView2Async(env, _s.Environment.CreateControllerOptions(env, IsPrivate));
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Creating tab", ex);
            ShowCrash("This tab couldn't start", "The browser engine failed to open this tab. Try reloading it.");
            _creating = null;
            return;
        }
        if (_disposed) { wv.Dispose(); return; }

        Configure(wv);
        wv.ZoomFactorChanged += (_, _) => OnPropertyChanged(nameof(Zoom));
        IsDiscarded = false;
        IsSleeping = false;
        if (_restoreZoom is { } z) { wv.ZoomFactor = z; _restoreZoom = null; }
        await UpdateYouTubeScriptAsync(_s.Blocker.Enabled);
        if (_disposed || !ReferenceEquals(WebView, wv)) return;
        UpdateLifecycle();
        if (_pendingUrl is not null)
        {
            var url = _pendingUrl;
            _pendingUrl = null;
            SafeNavigate(url);
        }
    }

    private void Configure(WebView2 wv)
    {
        var core = wv.CoreWebView2;
        var st = core.Settings;
        st.AreHostObjectsAllowed = false;          // never expose native objects to pages
        st.IsWebMessageEnabled = true;             // only honoured for the trusted new tab page (see OnWebMessage)
        st.AreDefaultScriptDialogsEnabled = true;
        st.AreDefaultContextMenusEnabled = true;
        st.AreDevToolsEnabled = true;
        st.IsStatusBarEnabled = true;
        st.IsZoomControlEnabled = true;
        st.AreBrowserAcceleratorKeysEnabled = true;
        st.IsBuiltInErrorPageEnabled = true;
        st.IsSwipeNavigationEnabled = true;

        try
        {
            core.SetVirtualHostNameToFolderMapping(UrlDisplay.NewTabHost, BrowserServices.NewTabFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(UrlDisplay.UserAssetsHost, _s.Paths.Backgrounds, CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(UrlDisplay.ReaderHost, BrowserServices.NewTabFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex) { _s.Paths.LogError("Mapping new tab page", ex); }

        ApplySettings();

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += (_, _) => UpdateNavState();
        core.DocumentTitleChanged += (_, _) => OnTitleChanged();
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync();
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(e);
        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        core.ContainsFullScreenElementChanged += (_, _) => FullScreenChanged?.Invoke(this, core.ContainsFullScreenElement);
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessage;
        core.PermissionRequested += (_, e) => _s.PermissionHandler.Handle(e, IsPrivate);
        core.DownloadStarting += (_, e) => _s.Downloads.Handle(e, IsPrivate, wv.Dispatcher);
        core.IsDocumentPlayingAudioChanged += (_, _) => IsPlayingAudio = core.IsDocumentPlayingAudio;
        core.IsMutedChanged += (_, _) => IsMuted = core.IsMuted;
        core.WebResourceRequested += OnWebResourceRequested;
        core.DOMContentLoaded += OnDomContentLoaded;
        UpdateBlockingFilter();
        // Certificate errors are intentionally NOT handled: Chromium's blocking interstitial stays in place.
    }

    /// <summary>Re-applies preferences that can change at runtime (theme, zoom, autofill).</summary>
    public void ApplySettings()
    {
        if (Core is not { } core || WebView is null) return;
        try
        {
            BrowserEnvironment.ApplyProfileSettings(core.Profile, _s.Settings.Current, _s.IsDarkTheme, _s.DownloadDirectory);
            WebView.DefaultBackgroundColor = _s.IsDarkTheme ? System.Drawing.Color.FromArgb(255, 32, 33, 36) : System.Drawing.Color.White;
        }
        catch (Exception ex) { _s.Paths.LogError("Applying settings", ex); }
    }

    public void ApplyDefaultZoom()
    {
        if (WebView is not null) WebView.ZoomFactor = _s.Settings.Current.DefaultZoom;
    }

    private bool _zoomApplied;
    private double? _restoreZoom;

    // ---- Sleeping tabs & thumbnails ------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _sleepTimer;

    partial void OnIsActiveChanged(bool value) { OnPropertyChanged(nameof(IsVisibleTab)); UpdateLifecycle(); }
    partial void OnIsSplitVisibleChanged(bool value) => UpdateLifecycle();

    /// <summary>
    /// Visible tabs run at full speed. Hidden tabs ask the engine to use less memory and, after a while,
    /// are suspended (no CPU, scripts paused) until they're shown again. Tabs playing audio never sleep.
    /// </summary>
    private void UpdateLifecycle()
    {
        if (Core is not { } core) return;
        try
        {
            if (IsVisibleTab)
            {
                _sleepTimer?.Stop();
                _discardTimer?.Stop();
                if (IsSleeping) { core.Resume(); IsSleeping = false; }
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                return;
            }
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            var discardMinutes = _s.Settings.Current.DiscardTabsAfterMinutes;
            if (discardMinutes > 0)
            {
                _discardTimer ??= new System.Windows.Threading.DispatcherTimer();
                _discardTimer.Stop();
                _discardTimer.Interval = TimeSpan.FromMinutes(discardMinutes);
                _discardTimer.Tick -= OnDiscardTimer;
                _discardTimer.Tick += OnDiscardTimer;
                _discardTimer.Start();
            }
            else _discardTimer?.Stop();
            var minutes = _s.Settings.Current.SleepTabsAfterMinutes;
            if (minutes <= 0) return;
            _sleepTimer ??= new System.Windows.Threading.DispatcherTimer();
            _sleepTimer.Stop();
            _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
            _sleepTimer.Tick -= OnSleepTimer;
            _sleepTimer.Tick += OnSleepTimer;
            _sleepTimer.Start();
        }
        catch (Exception ex) { _s.Paths.LogError("Tab lifecycle", ex); }
    }

    private async void OnSleepTimer(object? sender, EventArgs e)
    {
        _sleepTimer?.Stop();
        if (Core is not { } core || IsVisibleTab || IsSleeping) return;
        if (IsPlayingAudio || IsLoading || IsNewTabPage) { _sleepTimer?.Start(); return; }
        try { IsSleeping = await core.TrySuspendAsync(); }
        catch { IsSleeping = false; }
    }

    /// <summary>Puts a background tab to sleep right away (tab menu).</summary>
    public async Task SleepNowAsync()
    {
        if (Core is not { } core || IsVisibleTab || IsSleeping) return;
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        try { IsSleeping = await core.TrySuspendAsync(); } catch { }
    }

    // ---- Memory saver: unloading tabs ------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _discardTimer;

    /// <summary>
    /// Whether the page can be unloaded without losing anything the user would miss: a hidden, idle,
    /// normal-window web page that isn't playing audio and while no download is running.
    /// </summary>
    public bool CanDiscard =>
        Core is not null && !IsVisibleTab && !IsPrivate && !IsPopupTarget && !IsNewTabPage && !IsCrashed
        && !IsPlayingAudio && !IsLoading && _s.Downloads.ActiveCount == 0
        && (IsReaderPage ? _readerOriginalUrl is not null : Url.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    private void OnDiscardTimer(object? sender, EventArgs e)
    {
        _discardTimer?.Stop();
        if (Core is null || IsVisibleTab) return;
        if (!Discard()) _discardTimer?.Start();
    }

    /// <summary>
    /// Closes the tab's page to give all of its memory back. Title, icon and preview stay in the tab strip;
    /// the page loads again from its address when the tab is opened.
    /// </summary>
    public bool Discard()
    {
        if (!CanDiscard) return false;
        var url = IsReaderPage && _readerOriginalUrl is not null ? _readerOriginalUrl : Url;
        _sleepTimer?.Stop();
        _discardTimer?.Stop();
        DisposeWebView();
        _creating = null;
        _filterInstalled = false;
        _youTubeScriptId = null;
        _restoreZoom = Zoom;
        _pendingUrl = url;
        Url = url;
        CanGoBack = false;
        CanGoForward = false;
        IsPlayingAudio = false;
        BlockedCount = 0;
        IsSleeping = true;
        IsDiscarded = true;
        return true;
    }

    /// <summary>Takes a small screenshot of the page for tab hover previews.</summary>
    public async Task CaptureThumbnailAsync()
    {
        if (Core is not { } core || !ShowWebView || IsNewTabPage) return;
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            ms.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 320;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Thumbnail = bmp;
        }
        catch { /* previews are optional */ }
    }

    // ---- Ad & tracker blocking -------------------------------------------------------------

    private string? _navigatingUri;
    private string _pageHost = string.Empty;
    private bool _filterInstalled;

    /// <summary>Installs or removes the request filter depending on whether blocking is on.</summary>
    public void UpdateBlockingFilter()
    {
        if (Core is not { } core) return;
        var want = _s.Blocker.Enabled;
        try
        {
            if (want && !_filterInstalled)
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                _filterInstalled = true;
            }
            else if (!want && _filterInstalled)
            {
                core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                _filterInstalled = false;
            }
        }
        catch (Exception ex) { _s.Paths.LogError("Blocker filter", ex); }
        IsBlockingActive = _s.Blocker.IsActiveFor(Url);
        _ = UpdateYouTubeScriptAsync(want);
    }

    private string? _youTubeScriptId;
    private bool _youTubeScriptWanted;
    private bool _youTubeScriptBusy;

    /// <summary>
    /// Adds or removes the YouTube ad remover. It runs before YouTube's own scripts in every new document,
    /// so it's registered once per tab and only takes effect on YouTube pages where blocking is on.
    /// </summary>
    private async Task UpdateYouTubeScriptAsync(bool want)
    {
        _youTubeScriptWanted = want;
        if (Core is not { } core || _youTubeScriptBusy) return;
        _youTubeScriptBusy = true;
        try
        {
            // Loop so a change requested while the engine was busy still wins.
            while (ReferenceEquals(Core, core) && _youTubeScriptWanted != (_youTubeScriptId is not null))
            {
                if (_youTubeScriptWanted)
                    _youTubeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdBlocker.Script);
                else
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(_youTubeScriptId!);
                    _youTubeScriptId = null;
                }
            }
        }
        catch (Exception ex) { _s.Paths.LogError("YouTube ad blocker", ex); }
        finally { _youTubeScriptBusy = false; }
    }

    private static Relay.Core.Blocking.RequestType MapContext(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Document => Relay.Core.Blocking.RequestType.Subdocument,
        CoreWebView2WebResourceContext.Stylesheet => Relay.Core.Blocking.RequestType.Stylesheet,
        CoreWebView2WebResourceContext.Image => Relay.Core.Blocking.RequestType.Image,
        CoreWebView2WebResourceContext.Media => Relay.Core.Blocking.RequestType.Media,
        CoreWebView2WebResourceContext.Font => Relay.Core.Blocking.RequestType.Font,
        CoreWebView2WebResourceContext.Script => Relay.Core.Blocking.RequestType.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch => Relay.Core.Blocking.RequestType.XmlHttpRequest,
        CoreWebView2WebResourceContext.Websocket => Relay.Core.Blocking.RequestType.WebSocket,
        CoreWebView2WebResourceContext.Ping => Relay.Core.Blocking.RequestType.Ping,
        _ => Relay.Core.Blocking.RequestType.Other,
    };

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsBlockingActive || Core is null) return;
        try
        {
            var uri = e.Request.Uri;
            // Never block the page the user is navigating to.
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document && uri == _navigatingUri) return;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
            var result = _s.Blocker.Engine.Match(u, _pageHost, MapContext(e.ResourceContext));
            if (!result.Blocked) return;
            e.Response = Core.Environment.CreateWebResourceResponse(null, 403, "Blocked by RELAY", "Content-Type: text/plain");
            BlockedCount++;
            _s.Blocker.CountBlocked();
        }
        catch (Exception) { /* a filtering error must never break the page */ }
    }

    private async void OnDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (!IsBlockingActive || Core is null || _pageHost.Length == 0) return;
        try
        {
            var selectors = _s.Blocker.Engine.SelectorsFor(_pageHost);
            if (selectors.Count == 0) return;
            var css = Relay.Core.Blocking.FilterEngine.BuildStylesheet(selectors);
            var js = "(function(){try{var s=document.createElement('style');s.setAttribute('data-relay','hide');s.textContent="
                     + System.Text.Json.JsonSerializer.Serialize(css)
                     + ";(document.head||document.documentElement).appendChild(s);}catch(e){}})();";
            await Core.ExecuteScriptAsync(js);
        }
        catch (Exception) { /* cosmetic only */ }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // A new top-level page: reset the blocker for it.
        _navigatingUri = e.Uri;
        _pageHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var nu) ? nu.Host : string.Empty;
        IsBlockingActive = _s.Blocker.IsActiveFor(e.Uri);
        // The YouTube ad remover follows the per-site switch too.
        if (YouTubeAdBlocker.IsYouTubeHost(_pageHost)) _ = UpdateYouTubeScriptAsync(IsBlockingActive);
        else if (_s.Blocker.Enabled && !_youTubeScriptWanted) _ = UpdateYouTubeScriptAsync(true);
        if (!e.IsRedirected) BlockedCount = 0;
        IsLoading = true;
        if (IsCrashed) IsCrashed = false;
        if (!_zoomApplied) { _zoomApplied = true; ApplyDefaultZoom(); }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoading = false;
        UpdateNavState();
        if (Core is null) return;
        OnTitleChanged();
        if (e.IsSuccess) await RecordHistoryAsync();
        if (e.IsSuccess) { await Task.Delay(1200); await CaptureThumbnailAsync(); }
    }

    private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (Core is null) return;
        Url = Core.Source;
        var host = UrlDisplay.Host(Url);
        if (!host.Equals(_lastFaviconHost, StringComparison.OrdinalIgnoreCase))
        {
            _lastFaviconHost = host;
            Favicon = null;
            if (host.Length > 0) await LoadCachedFaviconAsync(Url);
        }
        // Same-document navigations (single-page apps) don't fire NavigationCompleted.
        if (!e.IsNewDocument) await RecordHistoryAsync();
    }

    private void OnTitleChanged()
    {
        if (Core is null) return;
        var t = Core.DocumentTitle;
        if (IsNewTabPage) t = "New tab";
        else if (IsReaderPage && string.IsNullOrWhiteSpace(t)) t = "Reader view";
        else if (string.IsNullOrWhiteSpace(t) || t == Core.Source) t = UrlDisplay.Host(Core.Source) is { Length: > 0 } h ? h : Core.Source;
        Title = t;
        if (!IsPrivate && !IsNewTabPage && !UrlDisplay.IsInternal(Url) && Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _ = _s.History.UpdateTitleAsync(Url, Title);
    }

    private async Task RecordHistoryAsync()
    {
        if (IsPrivate || Core is null) return;
        var url = Core.Source;
        if (UrlDisplay.IsInternal(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
            return;
        try { await _s.History.RecordVisitAsync(url, Core.DocumentTitle ?? string.Empty); }
        catch (Exception ex) { _s.Paths.LogError("Recording history", ex); }
    }

    private void UpdateNavState()
    {
        if (Core is null) return;
        CanGoBack = Core.CanGoBack;
        CanGoForward = Core.CanGoForward;
    }

    private async Task LoadCachedFaviconAsync(string url)
    {
        var img = await _s.FaviconCache.GetForUrlAsync(url);
        if (img is not null && UrlDisplay.Host(Url).Equals(UrlDisplay.Host(url), StringComparison.OrdinalIgnoreCase)) Favicon = img;
    }

    private async Task LoadFaviconAsync()
    {
        if (Core is null) return;
        try
        {
            if (string.IsNullOrEmpty(Core.FaviconUri)) return;
            var sourceUrl = Core.Source;
            using var stream = await Core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null) return;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return;
            var img = FaviconCache.Decode(bytes);
            if (img is null || Core is null || Core.Source != sourceUrl) return;
            Favicon = img;
            if (!IsPrivate && !UrlDisplay.IsInternal(sourceUrl))
            {
                _s.FaviconCache.Put(sourceUrl, img);
                await _s.Favicons.SetAsync(UrlDisplay.Host(sourceUrl), bytes);
            }
        }
        catch (Exception) { /* favicons are cosmetic */ }
    }

    private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (NewWindowRequested is null) { e.Handled = true; return; }
        NewWindowRequested.Invoke(this, e);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ShowCrash("This page stopped working", "Something went wrong while displaying this page. Reload to try again.");
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowCrash("This page isn't responding", "The page is taking too long to respond. You can reload it or wait.");
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // BrowserEnvironment raises Reset; the window recreates every tab.
                ShowCrash("The browser engine restarted", "RELAY is reopening this tab.");
                break;
            default:
                // GPU/utility/frame processes are restarted by Chromium automatically.
                break;
        }
    }

    private void ShowCrash(string title, string message)
    {
        CrashTitle = title;
        CrashMessage = message;
        IsLoading = false;
        IsCrashed = true;
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; } catch { return; }
        if (json.Length > 64 * 1024) return;
        // The reader view may only ask for its article or to close itself.
        if (IsReaderPage && UrlDisplay.IsReader(e.Source)) { OnReaderMessage(json); return; }
        // Only RELAY's own new tab page, as the top-level document, may talk to the browser.
        if (!NewTabService.IsTrustedSource(e.Source) || !IsNewTabPage) return;
        try { await _s.NewTab.HandleMessageAsync(this, json); }
        catch (Exception ex) { _s.Paths.LogError("New tab message", ex); }
    }

    private async void OnNewTabStateChanged(object? sender, EventArgs e)
    {
        if (IsNewTabPage) await PushNewTabStateAsync();
    }

    public async Task PushNewTabStateAsync()
    {
        if (Core is null || !IsNewTabPage) return;
        try
        {
            var json = await _s.NewTab.BuildStateJsonAsync(IsPrivate);
            if (Core is not null && IsNewTabPage) Core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { _s.Paths.LogError("New tab state", ex); }
    }

    public async Task PostNewTabMessageAsync(string json)
    {
        await Task.Yield();
        if (Core is not null && IsNewTabPage) Core.PostWebMessageAsJson(json);
    }

    // ---- Commands -------------------------------------------------------------------------

    public void Navigate(string url)
    {
        if (Core is null)
        {
            _pendingUrl = url;
            Url = url;
            _ = EnsureCreatedAsync();
            return;
        }
        SafeNavigate(url);
    }

    private void SafeNavigate(string url)
    {
        try
        {
            IsCrashed = false;
            Core?.Navigate(url);
        }
        catch (ArgumentException)
        {
            // Malformed URL: treat as a search instead of failing.
            var r = AddressParser.Parse(url, _s.SearchEngine);
            if (r is not null && r.Uri.AbsoluteUri != url) Core?.Navigate(r.Uri.AbsoluteUri);
        }
        catch (Exception ex) { _s.Paths.LogError("Navigate", ex); }
    }

    public void GoBack() { if (Core?.CanGoBack == true) Core.GoBack(); }
    public void GoForward() { if (Core?.CanGoForward == true) Core.GoForward(); }
    public void Stop() { Core?.Stop(); IsLoading = false; }

    public void Reload()
    {
        if (Core is null) { _ = EnsureCreatedAsync(); return; }
        IsCrashed = false;
        try { Core.Reload(); } catch (Exception ex) { _s.Paths.LogError("Reload", ex); }
    }

    public void ToggleMute()
    {
        if (Core is not null) Core.IsMuted = !Core.IsMuted;
    }

    public void SetZoom(double factor)
    {
        if (WebView is not null) WebView.ZoomFactor = Math.Clamp(factor, 0.25, 5);
    }

    public double Zoom => WebView?.ZoomFactor ?? 1;

    public void OpenDevTools() => Core?.OpenDevToolsWindow();

    public async Task PrintAsync()
    {
        if (Core is null) return;
        try { Core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { _s.Paths.LogError("Print", ex); }
        await Task.CompletedTask;
    }

    /// <summary>Opens Chromium's built-in find bar for this page.</summary>
    public async Task FindAsync()
    {
        if (Core is null) return;
        try
        {
            var options = Core.Environment.CreateFindOptions();
            options.ShouldHighlightAllMatches = true;
            WebView?.Focus();
            await Core.Find.StartAsync(options);
        }
        catch (Exception ex) { _s.Paths.LogError("Find", ex); }
    }

    /// <summary>Called after the engine was restarted: builds a fresh WebView at the same URL.</summary>
    public async Task RecreateAsync()
    {
        var url = string.IsNullOrEmpty(Url) ? UrlDisplay.NewTabUrl : Url;
        DisposeWebView();
        _creating = null;
        _filterInstalled = false;
        _youTubeScriptId = null;
        _pendingUrl = url;
        IsCrashed = false;
        await EnsureCreatedAsync();
    }

    private void DisposeWebView()
    {
        var wv = WebView;
        if (wv is null) return;
        WebView = null;
        try
        {
            if (wv.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.SourceChanged -= OnSourceChanged;
                core.ProcessFailed -= OnProcessFailed;
                core.WebResourceRequested -= OnWebResourceRequested;
                core.DOMContentLoaded -= OnDomContentLoaded;
                core.WebMessageReceived -= OnWebMessage;
            }
        }
        catch { /* core may already be gone */ }
        try { wv.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sleepTimer?.Stop();
        _discardTimer?.Stop();
        _s.NewTab.StateChanged -= OnNewTabStateChanged;
        DisposeWebView();
    }
}
