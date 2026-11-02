using System.Windows;
using System.Windows.Threading;
using Relay.App.Views;
using Relay.Browser;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.App.Services;

/// <summary>Tracks browser windows, saves the session, and shuts the app down when the last window closes.</summary>
public sealed class WindowManager
{
    private readonly BrowserServices _s;
    private readonly List<MainWindow> _windows = [];
    private readonly DispatcherTimer _saveTimer;
    private bool _shuttingDown;

    public WindowManager(BrowserServices services)
    {
        _s = services;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveSessionAsync(); };
        _s.Environment.Reset += (_, _) => Application.Current.Dispatcher.BeginInvoke(RecoverFromEngineCrash);
    }

    public IReadOnlyList<MainWindow> All => _windows;
    public MainWindow? LastActive { get; private set; }

    public MainWindow Open(bool isPrivate, IEnumerable<SessionTab>? tabs = null, IEnumerable<string>? urls = null)
    {
        var w = new MainWindow(_s, isPrivate);
        _windows.Add(w);
        LastActive = w;

        var added = false;
        if (tabs is not null)
        {
            BrowserTab? active = null;
            var restored = new List<(BrowserTab, SessionTab)>();
            foreach (var t in tabs)
            {
                var tab = w.ViewModel.AddTab(UrlDisplay.IsNewTab(t.Url) ? null : t.Url, activate: false, lazy: true);
                if (!string.IsNullOrEmpty(t.Title)) tab.Title = t.Title;
                if (t.IsActive) active = tab;
                restored.Add((tab, t));
                added = true;
            }
            if (added) w.ViewModel.ActiveTab = active ?? w.ViewModel.Tabs[0];
            w.ViewModel.RestoreGroups(restored);
        }
        foreach (var u in urls ?? [])
        {
            w.ViewModel.AddTab(u, activate: true);
            added = true;
        }
        if (!added) w.ViewModel.NewTab();

        w.Activated += (_, _) => LastActive = w;
        w.ViewModel.TabsChanged += (_, _) => { if (!isPrivate) ScheduleSave(); };
        w.Closed += (_, _) => OnClosed(w);
        w.Show();
        return w;
    }

    public bool IsLastNormalWindow(MainWindow w) => !w.IsPrivate && _windows.Count(x => !x.IsPrivate) == 1 && _windows.Contains(w);

    /// <summary>Called while a window is closing (before it's removed).</summary>
    public void OnClosing(MainWindow w)
    {
        if (w.IsPrivate) return;
        _saveTimer.Stop();
        if (IsLastNormalWindow(w))
        {
            // Last normal window: its tabs become the session to restore next time.
            SaveSessionSync(includeClosing: true, closing: w);
        }
        else
        {
            SaveSessionSync(includeClosing: false, closing: w);
        }
    }

    private void OnClosed(MainWindow w)
    {
        _windows.Remove(w);
        if (ReferenceEquals(LastActive, w)) LastActive = _windows.LastOrDefault();
        if (_windows.All(x => !x.IsPrivate)) _s.Downloads.ForgetPrivate();
        if (_windows.Count == 0 && !_shuttingDown)
        {
            _shuttingDown = true;
            App.Current.CloseAuxiliaryWindows();
            Application.Current.Shutdown();
        }
    }

    public void CloseAll()
    {
        foreach (var w in _windows.ToList()) w.Close();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private List<SessionTab> Collect(MainWindow? exclude)
    {
        var list = new List<SessionTab>();
        var index = 0;
        foreach (var w in _windows.Where(x => !x.IsPrivate && !ReferenceEquals(x, exclude)))
            list.AddRange(w.ViewModel.Snapshot(index++));
        return list;
    }

    private async Task SaveSessionAsync()
    {
        if (_s.Settings.Current.ClearDataOnExit) return;
        try { await _s.Sessions.SaveAsync(Collect(null)); }
        catch (Exception ex) { _s.Paths.LogError("Saving session", ex); }
    }

    private void SaveSessionSync(bool includeClosing, MainWindow closing)
    {
        try
        {
            var tabs = _s.Settings.Current.ClearDataOnExit ? [] : Collect(includeClosing ? null : closing);
            _s.Sessions.Save(tabs);
        }
        catch (Exception ex) { _s.Paths.LogError("Saving session", ex); }
    }

    /// <summary>The shared browser process died: rebuild every tab's WebView at its last URL.</summary>
    private async void RecoverFromEngineCrash()
    {
        foreach (var w in _windows.ToList())
            foreach (var tab in w.ViewModel.Tabs.ToList())
            {
                try { if (tab.WebView is not null || tab.IsActive) await tab.RecreateAsync(); }
                catch (Exception ex) { _s.Paths.LogError("Recovering tab", ex); }
            }
    }
}
