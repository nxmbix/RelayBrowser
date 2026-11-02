using Relay.Core.Blocking;
using Xunit;

namespace Relay.Tests;

public class FilterEngineTests
{
    private static FilterEngine Engine(params string[] rules)
    {
        var e = new FilterEngine();
        e.AddRules(rules);
        return e;
    }

    private static bool Blocks(FilterEngine e, string url, string page = "news.example.org", RequestType type = RequestType.Script) =>
        e.Match(new Uri(url), page, type).Blocked;

    [Fact]
    public void Host_rules_match_host_and_subdomains_only()
    {
        var e = Engine("||doubleclick.net^");
        Assert.True(Blocks(e, "https://doubleclick.net/x.js"));
        Assert.True(Blocks(e, "https://ad.g.doubleclick.net/pagead/x"));
        Assert.False(Blocks(e, "https://notdoubleclick.net/x.js"));
        Assert.False(Blocks(e, "https://example.com/?u=doubleclick.net"));
    }

    [Fact]
    public void Exceptions_override_blocks()
    {
        var e = Engine("||tracker.example^", "@@||tracker.example/allowed/*");
        Assert.True(Blocks(e, "https://tracker.example/pixel.gif"));
        Assert.False(Blocks(e, "https://tracker.example/allowed/script.js"));
    }

    [Fact]
    public void Path_patterns_with_wildcards_and_separators()
    {
        var e = Engine("/ads/banner*.gif", "-advert-", "||cdn.example.com/track^");
        Assert.True(Blocks(e, "https://site.com/ads/banner728.gif", type: RequestType.Image));
        Assert.True(Blocks(e, "https://site.com/img/top-advert-1.png", type: RequestType.Image));
        Assert.True(Blocks(e, "https://cdn.example.com/track?id=1"));
        Assert.False(Blocks(e, "https://cdn.example.com/tracking.js"));
        Assert.False(Blocks(e, "https://site.com/downloads/banner.gif", type: RequestType.Image));
    }

    [Fact]
    public void Third_party_option()
    {
        var e = Engine("||widgets.social.example^$third-party");
        Assert.True(Blocks(e, "https://widgets.social.example/w.js", page: "blog.example.org"));
        Assert.False(Blocks(e, "https://widgets.social.example/w.js", page: "www.social.example"));
    }

    [Fact]
    public void Type_options()
    {
        var e = Engine("||img.ads.example^$image", "||x.example^$~script");
        Assert.True(Blocks(e, "https://img.ads.example/a.png", type: RequestType.Image));
        Assert.False(Blocks(e, "https://img.ads.example/a.js", type: RequestType.Script));
        Assert.False(Blocks(e, "https://x.example/a.js", type: RequestType.Script));
        Assert.True(Blocks(e, "https://x.example/a.png", type: RequestType.Image));
    }

    [Fact]
    public void Domain_option()
    {
        var e = Engine("/sponsor.js$domain=news.example|~sub.news.example");
        Assert.True(Blocks(e, "https://cdn.x/sponsor.js", page: "news.example"));
        Assert.True(Blocks(e, "https://cdn.x/sponsor.js", page: "www.news.example"));
        Assert.False(Blocks(e, "https://cdn.x/sponsor.js", page: "sub.news.example"));
        Assert.False(Blocks(e, "https://cdn.x/sponsor.js", page: "other.example"));
    }

    [Fact]
    public void Never_blocks_pages_and_skips_unsupported_rules()
    {
        var e = Engine("||evil.example^$document", "||x.example^$redirect=noop.js", "/^https?:\\/\\/[a-z]+\\.ads\\./", "##.generic-ad");
        Assert.Equal(0, e.NetworkRuleCount);
        Assert.Equal(0, e.CosmeticRuleCount);
    }

    [Fact]
    public void Document_exceptions_allow_whole_page()
    {
        var e = Engine("@@||intranet.example^$document");
        Assert.True(e.IsPageAllowed(new Uri("https://intranet.example/home")));
        Assert.False(e.IsPageAllowed(new Uri("https://other.example/")));
    }

    [Fact]
    public void Cosmetic_rules_are_domain_specific()
    {
        var e = Engine("example.com##.sidebar-ad", "example.com,other.org##div[id^=\"promo\"]", "shop.example.com#@#.sidebar-ad");
        Assert.Contains(".sidebar-ad", e.SelectorsFor("www.example.com"));
        Assert.DoesNotContain(".sidebar-ad", e.SelectorsFor("shop.example.com"));
        Assert.Contains("div[id^=\"promo\"]", e.SelectorsFor("other.org"));
        Assert.Empty(e.SelectorsFor("unrelated.net"));
        Assert.Contains("{display:none!important}", FilterEngine.BuildStylesheet(e.SelectorsFor("example.com")));
    }

    [Fact]
    public void Tokens_next_to_wildcards_still_match()
    {
        var e = Engine("banner*track", "ads.js");
        Assert.True(Blocks(e, "https://a.com/bigbanner/xx/tracker"));
        Assert.True(Blocks(e, "https://a.com/static/myads.js"));
    }

    [Fact]
    public void Base_domain()
    {
        Assert.Equal("bbc.co.uk", FilterEngine.BaseDomain("news.bbc.co.uk"));
        Assert.Equal("example.com", FilterEngine.BaseDomain("a.b.example.com"));
        Assert.Equal("localhost", FilterEngine.BaseDomain("localhost"));
    }
}
