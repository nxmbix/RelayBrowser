using System.Windows;
using Microsoft.Web.WebView2.Core;
using Relay.App.Controls;
using Relay.Browser;

namespace Relay.App.Views;

/// <summary>"Clear browsing data": RELAY's own history plus Chromium's cookies, cache and site storage.</summary>
public static class ClearDataDialog
{
    /// <summary>The live normal (non-private) WebView2 profile, if any tab has been created.</summary>
    public static CoreWebView2Profile? NormalProfile =>
        App.Current.WindowManager?.All.Where(w => !w.IsPrivate)
            .SelectMany(w => w.ViewModel.Tabs)
            .Select(t => t.Core)
            .FirstOrDefault(c => c is not null)?.Profile;

    public static CoreWebView2CookieManager? NormalCookieManager =>
        App.Current.WindowManager?.All.Where(w => !w.IsPrivate)
            .SelectMany(w => w.ViewModel.Tabs)
            .Select(t => t.Core)
            .FirstOrDefault(c => c is not null)?.CookieManager;

    public static async Task ShowAsync(Window owner, BrowserServices s)
    {
        var ranges = new List<(object Value, string Text)>
        {
            (1, "Last hour"), (24, "Last 24 hours"), (24 * 7, "Last 7 days"), (24 * 28, "Last 4 weeks"), (0, "All time"),
        };
        var d = new RelayDialog(owner, "Clear browsing data", null, Icons.Clear)
            .AddChoice("Time range", ranges, 1)
            .AddCheck("Browsing history", true, "history")
            .AddCheck("Cookies and other site data", true, "cookies")
            .AddCheck("Cached images and files", true, "cache")
            .AddCheck("Download history", false, "downloads")
            .AddCheck("Saved passwords and autofill data", false, "autofill")
            .AddCheck("Site permissions", false, "permissions")
            .AddNote("Clearing cookies signs you out of most sites. Private windows are never stored, so there's nothing to clear for them.")
            .AddButton("cancel", "Cancel", isCancel: true)
            .AddButton("clear", "Clear data", primary: true);
        if (d.Ask() != "clear") return;

        var hours = d.Choice is int h ? h : 0;
        var since = hours == 0 ? (DateTime?)null : DateTime.UtcNow.AddHours(-hours);
        try
        {
            await ClearAsync(s, since,
                history: d.IsChecked("history"), cookies: d.IsChecked("cookies"), cache: d.IsChecked("cache"),
                downloads: d.IsChecked("downloads"), autofill: d.IsChecked("autofill"), permissions: d.IsChecked("permissions"));
            RelayDialog.Info(owner, "Browsing data cleared", "The selected data was removed.", Icons.Check);
        }
        catch (Exception ex)
        {
            s.Paths.LogError("Clear data", ex);
            RelayDialog.Info(owner, "Couldn't clear everything", ex.Message, Icons.Warning);
        }
    }

    public static Task ClearEverythingAsync(BrowserServices s) =>
        ClearAsync(s, null, history: true, cookies: true, cache: true, downloads: true, autofill: false, permissions: false);

    public static async Task ClearAsync(BrowserServices s, DateTime? sinceUtc, bool history, bool cookies, bool cache, bool downloads, bool autofill, bool permissions)
    {
        if (history)
        {
            await s.History.ClearAsync(sinceUtc);
            s.FaviconCache.Clear();
            if (sinceUtc is null) await s.Favicons.ClearAsync();
        }
        if (downloads) await s.Downloads.ClearFinishedAsync();
        if (permissions) await s.Permissions.ClearAsync();

        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (cookies) kinds |= CoreWebView2BrowsingDataKinds.AllSite;
        if (cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        if (history) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
        if (downloads) kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
        if (autofill) kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill | CoreWebView2BrowsingDataKinds.PasswordAutosave;
        if (kinds == 0) return;

        var profile = NormalProfile;
        if (profile is null) return; // no live tab: Chromium data can be cleared next time a page is open
        if (sinceUtc is null) await profile.ClearBrowsingDataAsync(kinds);
        else await profile.ClearBrowsingDataAsync(kinds, sinceUtc.Value.ToLocalTime(), DateTime.Now);
    }
}
