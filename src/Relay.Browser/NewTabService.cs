using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Navigation;

namespace Relay.Browser;

/// <summary>
/// Data bridge for RELAY's new tab page (https://newtab.relay/). The page is local HTML/JS;
/// it receives its data as JSON and asks for changes through validated messages.
/// No native objects are exposed to web content.
/// </summary>
public sealed class NewTabService
{
    private readonly BrowserServices _s;

    public NewTabService(BrowserServices services)
    {
        _s = services;
        _s.QuickLinks.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _s.Bookmarks.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _s.Settings.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tabs showing the new tab page should refresh their data.</summary>
    public event EventHandler? StateChanged;

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public static bool IsTrustedSource(string? source) =>
        source is not null && Uri.TryCreate(source, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps && u.Host.Equals(UrlDisplay.NewTabHost, StringComparison.OrdinalIgnoreCase);

    public async Task<string> BuildStateJsonAsync(bool isPrivate)
    {
        var settings = _s.Settings.Current;
        var state = new JsonObject
        {
            ["type"] = "state",
            ["private"] = isPrivate,
            ["theme"] = _s.IsDarkTheme ? "dark" : "light",
            ["accent"] = _s.AccentHex,
            ["showClock"] = settings.NewTabShowClock,
            ["showRecent"] = settings.NewTabShowRecent,
            ["showBookmarks"] = settings.NewTabShowBookmarks,
            ["engine"] = _s.SearchEngine.Name,
            ["blockedTotal"] = _s.Blocker.Enabled ? _s.Blocker.TotalBlocked : 0,
            ["background"] = BackgroundValue(settings.NewTabBackground, settings.NewTabBackgroundImage),
        };

        var links = new JsonArray();
        foreach (var q in await _s.QuickLinks.GetAllAsync())
            links.Add(new JsonObject { ["id"] = q.Id, ["title"] = q.Title, ["url"] = q.Url, ["host"] = UrlDisplay.Host(q.Url) });
        state["quickLinks"] = links;

        var recent = new JsonArray();
        if (!isPrivate && settings.NewTabShowRecent)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in await _s.History.RecentPagesAsync(60))
            {
                var host = UrlDisplay.Host(h.Url);
                if (host.Length == 0 || !seen.Add(host)) continue;
                recent.Add(new JsonObject { ["title"] = string.IsNullOrWhiteSpace(h.Title) ? host : h.Title, ["url"] = h.Url, ["host"] = host });
                if (recent.Count >= 6) break;
            }
        }
        state["recent"] = recent;

        var bookmarks = new JsonArray();
        if (settings.NewTabShowBookmarks)
        {
            foreach (var b in (await _s.Bookmarks.GetAllAsync()).Where(b => !b.IsFolder && b.Url is not null).OrderByDescending(b => b.CreatedAt).Take(8))
                bookmarks.Add(new JsonObject { ["title"] = b.Title, ["url"] = b.Url, ["host"] = UrlDisplay.Host(b.Url) });
        }
        state["bookmarks"] = bookmarks;
        return state.ToJsonString();
    }

    private static string BackgroundValue(string id, string? image)
    {
        if (id == "custom" && !string.IsNullOrEmpty(image))
            return $"https://{UrlDisplay.UserAssetsHost}/{Uri.EscapeDataString(Path.GetFileName(image))}";
        return id;
    }

    /// <summary>Handles a message from the page. Unknown or malformed messages are ignored.</summary>
    public async Task HandleMessageAsync(BrowserTab tab, string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (msg is not JsonObject o) return;

        string Str(string key, int max = 2048)
        {
            var v = o[key] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : string.Empty;
            return v.Length > max ? v[..max] : v;
        }
        long Id() => o["id"] is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : 0;

        switch (Str("type", 64))
        {
            case "ready":
                await tab.PushNewTabStateAsync();
                break;

            case "navigate":
            {
                var result = AddressParser.Parse(Str("text"), _s.SearchEngine);
                if (result is not null) tab.Navigate(result.Uri.AbsoluteUri);
                break;
            }

            case "open":
            {
                // Only http(s) links from our own lists.
                var url = Str("url");
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                    tab.Navigate(u.AbsoluteUri);
                break;
            }

            case "quicklink.save":
            {
                var title = Str("title", 120).Trim();
                var parsed = AddressParser.Parse(Str("url"), _s.SearchEngine);
                if (parsed is null || parsed.IsSearch) { await tab.PostNewTabMessageAsync("""{"type":"error","message":"Enter a valid web address."}"""); return; }
                var url = parsed.Uri.AbsoluteUri;
                if (title.Length == 0) title = UrlDisplay.Host(url);
                var id = Id();
                if (id > 0) await _s.QuickLinks.UpdateAsync(id, title, url);
                else await _s.QuickLinks.AddAsync(title, url);
                break;
            }

            case "quicklink.remove":
                if (Id() > 0) await _s.QuickLinks.DeleteAsync(Id());
                break;

            case "quicklink.reorder":
                if (o["ids"] is JsonArray arr)
                {
                    var ids = arr.Select(n => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0).Where(l => l > 0).Distinct().Take(100).ToList();
                    if (ids.Count > 0) await _s.QuickLinks.ReorderAsync(ids);
                }
                break;

            case "settings":
                _s.Ui.OpenSettings(Str("section", 32));
                break;

            case "background":
            {
                var value = Str("value", 32);
                if (value is "none" or "dawn" or "mist" or "dusk" or "forest" or "slate" or "sand")
                    _s.Settings.Update(s => s.NewTabBackground = value);
                break;
            }

            case "toggle":
            {
                var key = Str("key", 32);
                var on = o["value"] is JsonValue b && b.TryGetValue<bool>(out var v) && v;
                _s.Settings.Update(s =>
                {
                    switch (key)
                    {
                        case "clock": s.NewTabShowClock = on; break;
                        case "recent": s.NewTabShowRecent = on; break;
                        case "bookmarks": s.NewTabShowBookmarks = on; break;
                    }
                });
                break;
            }
        }
    }
}
