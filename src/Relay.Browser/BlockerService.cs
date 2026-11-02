using System.Net.Http;
using Relay.Core.Blocking;
using Relay.Core.Navigation;

namespace Relay.Browser;

public sealed record FilterList(string Id, string Name, string Description, string Url);

/// <summary>
/// RELAY's built-in ad and tracker blocker. Keeps filter lists in the data folder, refreshes them
/// every few days, and answers "should this request be blocked?" for every tab.
/// </summary>
public sealed class BlockerService
{
    public static IReadOnlyList<FilterList> Lists { get; } =
    [
        new("easylist", "EasyList", "Blocks most advertising.", "https://easylist.to/easylist/easylist.txt"),
        new("easyprivacy", "EasyPrivacy", "Blocks tracking scripts, analytics and tracking pixels.", "https://easylist.to/easylist/easyprivacy.txt"),
        new("adservers", "Peter Lowe's ad servers", "A compact list of known ad and tracking servers.",
            "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
        new("cookies", "Cookie notices", "Hides many cookie consent banners (EasyList Cookie).", "https://secure.fanboy.co.nz/fanboy-cookiemonster.txt"),
    ];

    /// <summary>Always-on fallback so blocking works before the first list download finishes.</summary>
    private static readonly string[] BuiltInRules =
    [
        "||doubleclick.net^", "||googlesyndication.com^", "||googleadservices.com^", "||google-analytics.com^$third-party",
        "||googletagmanager.com^$third-party", "||googletagservices.com^", "||adservice.google.com^", "||pagead2.googlesyndication.com^",
        "||amazon-adsystem.com^", "||adnxs.com^", "||adsrvr.org^", "||criteo.com^", "||criteo.net^", "||taboola.com^", "||outbrain.com^",
        "||scorecardresearch.com^", "||quantserve.com^", "||moatads.com^", "||rubiconproject.com^", "||pubmatic.com^", "||openx.net^",
        "||casalemedia.com^", "||advertising.com^", "||adform.net^", "||smartadserver.com^", "||yieldmo.com^", "||sharethrough.com^",
        "||media.net^$third-party", "||hotjar.com^$third-party", "||mouseflow.com^", "||fullstory.com^$third-party", "||clarity.ms^$third-party",
        "||connect.facebook.net^$third-party", "||facebook.com/tr^", "||analytics.tiktok.com^", "||ads-twitter.com^", "||static.ads-twitter.com^",
        "||bat.bing.com^", "||ads.linkedin.com^", "||px.ads.linkedin.com^", "||snap.licdn.com^$third-party", "||sc-static.net^$third-party",
        "||branch.io^$third-party", "||adjust.com^$third-party", "||appsflyer.com^$third-party", "||mixpanel.com^$third-party",
        "||segment.io^$third-party", "||cdn.segment.com^$third-party", "||newrelic.com^$third-party", "||nr-data.net^",
        "||chartbeat.com^", "||chartbeat.net^", "||parsely.com^$third-party", "||zedo.com^", "||popads.net^", "||propellerads.com^",
        "||adcolony.com^", "||unityads.unity3d.com^", "||mopub.com^", "||inmobi.com^", "||teads.tv^", "||2mdn.net^", "||serving-sys.com^",
        "||adroll.com^", "||bidswitch.net^", "||contextweb.com^", "||lijit.com^", "||sovrn.com^", "||33across.com^", "||indexww.com^",
        "||everesttech.net^", "||demdex.net^", "||omtrdc.net^", "||krxd.net^", "||bluekai.com^", "||exelator.com^", "||eyeota.net^",
        "||rlcdn.com^", "||tapad.com^", "||agkn.com^", "||mathtag.com^", "||turn.com^", "||yandex.ru/ads^", "||mc.yandex.ru^$third-party",
    ];

    private static readonly HttpClient Http = CreateClient();
    private readonly BrowserServices _s;
    private readonly string _folder;
    private volatile FilterEngine _engine = new();
    private int _pendingTotal;
    private readonly System.Threading.Timer _saveTimer;
    private Task? _updating;

    public BlockerService(BrowserServices services)
    {
        _s = services;
        _folder = Path.Combine(services.Paths.Root, "FilterLists");
        Directory.CreateDirectory(_folder);
        _engine.AddRules(BuiltInRules);
        _saveTimer = new System.Threading.Timer(_ => FlushTotal(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public FilterEngine Engine => _engine;
    public bool IsLoaded { get; private set; }
    public string Status { get; private set; } = "Loading filter lists…";

    /// <summary>Raised (on a background thread) when the rules or the on/off state change.</summary>
    public event EventHandler? Changed;

    public bool Enabled => _s.Settings.Current.BlockerEnabled;

    public long TotalBlocked => _s.Settings.Current.BlockedTotal + _pendingTotal;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RELAY/1.1");
        return c;
    }

    /// <summary>Loads cached lists, then refreshes them in the background when they're missing or old.</summary>
    public async Task InitializeAsync()
    {
        await RebuildAsync();
        var last = _s.Settings.Current.BlockerLastUpdated;
        var missing = _s.Settings.Current.BlockerLists.Any(id => !File.Exists(ListPath(id)));
        if (missing || last is null || DateTime.UtcNow - last.Value > TimeSpan.FromDays(4))
            _ = UpdateAsync();
    }

    private string ListPath(string id) => Path.Combine(_folder, id + ".txt");

    public DateTime? ListDate(string id) => File.Exists(ListPath(id)) ? File.GetLastWriteTimeUtc(ListPath(id)) : null;

    /// <summary>Rebuilds the engine from cached lists + custom rules (call after changing settings).</summary>
    public Task RebuildAsync() => Task.Run(() =>
    {
        try
        {
            var engine = new FilterEngine();
            engine.AddRules(BuiltInRules);
            foreach (var id in _s.Settings.Current.BlockerLists)
            {
                var path = ListPath(id);
                if (File.Exists(path)) engine.AddRules(File.ReadLines(path));
            }
            engine.AddRules(_s.Settings.Current.BlockerCustomRules.Split('\n'));
            _engine = engine;
            IsLoaded = true;
            Status = $"{engine.NetworkRuleCount:N0} blocking rules and {engine.CosmeticRuleCount:N0} hiding rules active.";
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Loading filter lists", ex);
            Status = "Couldn't load filter lists. Built-in protection is still active.";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Downloads the enabled lists. Safe to call repeatedly; concurrent calls share one update.</summary>
    public Task UpdateAsync() => _updating is { IsCompleted: false } running ? running : _updating = DoUpdateAsync();

    private async Task DoUpdateAsync()
    {
        Status = "Updating filter lists…";
        Changed?.Invoke(this, EventArgs.Empty);
        var failures = 0;
        foreach (var id in _s.Settings.Current.BlockerLists.ToList())
        {
            var list = Lists.FirstOrDefault(l => l.Id == id);
            if (list is null) continue;
            try
            {
                using var resp = await Http.GetAsync(list.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > 20_000_000) throw new InvalidDataException("List is too large.");
                var text = await resp.Content.ReadAsStringAsync();
                if (text.Length > 20_000_000) throw new InvalidDataException("List is too large.");
                // Sanity check: a filter list, not an error page.
                var head = text.TrimStart();
                if (head.StartsWith('<')) throw new InvalidDataException("The server returned a web page instead of a filter list.");
                var tmp = ListPath(id) + ".tmp";
                await File.WriteAllTextAsync(tmp, text);
                File.Move(tmp, ListPath(id), overwrite: true);
            }
            catch (Exception ex)
            {
                failures++;
                _s.Paths.LogError($"Updating filter list {id}", ex);
            }
        }
        if (failures < _s.Settings.Current.BlockerLists.Count)
        {
            _s.Settings.Current.BlockerLastUpdated = DateTime.UtcNow;
            try { _s.Settings.Save(); } catch { }
        }
        await RebuildAsync();
        if (failures > 0) Status += $" {failures} list(s) couldn't be updated — check your connection.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Per-site control -------------------------------------------------------------

    public static string SiteKey(string host) => FilterEngine.BaseDomain(host.ToLowerInvariant().TrimStart('.'));

    public bool IsSiteAllowed(string host) =>
        host.Length > 0 && _s.Settings.Current.BlockerAllowedSites.Contains(SiteKey(host), StringComparer.OrdinalIgnoreCase);

    public void SetSiteAllowed(string host, bool allowed)
    {
        var key = SiteKey(host);
        if (key.Length == 0) return;
        _s.Settings.Update(s =>
        {
            s.BlockerAllowedSites.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (allowed) s.BlockerAllowedSites.Add(key);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether blocking should run for a page at all.</summary>
    public bool IsActiveFor(string? pageUrl)
    {
        if (!Enabled || pageUrl is null || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        if (UrlDisplay.IsInternal(pageUrl)) return false;
        if (IsSiteAllowed(u.Host)) return false;
        return !_engine.IsPageAllowed(u);
    }

    public void CountBlocked() => Interlocked.Increment(ref _pendingTotal);

    private void FlushTotal()
    {
        var n = Interlocked.Exchange(ref _pendingTotal, 0);
        if (n == 0) return;
        _s.Settings.Current.BlockedTotal += n;
        try { _s.Settings.Save(); } catch { /* retried on next tick */ }
    }

    public void Shutdown()
    {
        _saveTimer.Dispose();
        FlushTotal();
    }
}
