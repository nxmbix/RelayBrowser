using Relay.Core.Navigation;
using Relay.Core.Search;
using Xunit;

namespace Relay.Tests;

public class AddressParserTests
{
    private static readonly SearchEngine Engine = SearchEngines.Google;

    [Theory]
    [InlineData("google.com", "https://google.com/")]
    [InlineData("youtube.com", "https://youtube.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://example.com/path?q=1", "http://example.com/path?q=1")]
    [InlineData("example.com/docs/page", "https://example.com/docs/page")]
    [InlineData("sub.domain.co.uk", "https://sub.domain.co.uk/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("192.168.1.1", "http://192.168.1.1/")]
    [InlineData("10.0.0.5:8080/admin", "http://10.0.0.5:8080/admin")]
    [InlineData("example.com:8443", "https://example.com:8443/")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("  github.com  ", "https://github.com/")]
    [InlineData("[::1]:8080", "http://[::1]:8080/")]
    public void Navigates_to_urls(string input, string expected)
    {
        var r = AddressParser.Parse(input, Engine)!;
        Assert.False(r.IsSearch);
        Assert.Equal(expected, r.Uri.ToString());
    }

    [Theory]
    [InlineData("best gaming laptops")]
    [InlineData("weather")]
    [InlineData("3.14")]
    [InlineData("1+1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("what is http: anyway")]
    [InlineData("hello.")]
    [InlineData("foo..com")]
    [InlineData("256.1.1.1")]
    [InlineData("?example.com")]
    public void Searches_everything_else(string input)
    {
        var r = AddressParser.Parse(input, Engine)!;
        Assert.True(r.IsSearch);
        Assert.StartsWith("https://www.google.com/search?q=", r.Uri.ToString());
    }

    [Fact]
    public void Encodes_search_queries()
    {
        var r = AddressParser.Parse("c# & .net", Engine)!;
        Assert.Equal("https://www.google.com/search?q=c%23%20%26%20.net", r.Uri.AbsoluteUri);
    }

    [Fact]
    public void Empty_input_returns_null()
    {
        Assert.Null(AddressParser.Parse("   ", Engine));
        Assert.Null(AddressParser.Parse(null, Engine));
    }

    [Fact]
    public void Uses_selected_engine()
    {
        var r = AddressParser.Parse("cats", SearchEngines.DuckDuckGo)!;
        Assert.Equal("https://duckduckgo.com/?q=cats", r.Uri.ToString());
    }

    [Fact]
    public void Custom_engine_requires_placeholder()
    {
        Assert.False(SearchEngine.IsValidTemplate("https://example.com/search"));
        Assert.True(SearchEngine.IsValidTemplate("https://example.com/search?q=%s"));
        Assert.Equal("google", SearchEngines.Resolve("custom", "nope").Id);
        Assert.Equal("custom", SearchEngines.Resolve("custom", "https://s.example/?q=%s").Id);
    }

    [Fact]
    public void Address_bar_hides_new_tab_url()
    {
        Assert.Equal("", UrlDisplay.ForAddressBar(UrlDisplay.NewTabUrl));
        Assert.Equal("example.com", UrlDisplay.Host("https://www.example.com/a"));
        Assert.Equal("https://example.com", UrlDisplay.Origin("https://Example.com/x?y"));
    }

    [Theory]
    [InlineData("relay://passwords")]
    [InlineData("RELAY://Passwords/")]
    [InlineData("relay:passwords")]
    public void Relay_urls_open_internal_pages(string input)
    {
        var r = AddressParser.Parse(input, Engine)!;
        Assert.False(r.IsSearch);
        Assert.Equal(UrlDisplay.PasswordsUrl, r.Uri.ToString());
    }

    [Fact]
    public void Passwords_page_shows_its_relay_address_and_is_internal()
    {
        Assert.Equal("relay://passwords", UrlDisplay.ForAddressBar(UrlDisplay.PasswordsUrl));
        Assert.True(UrlDisplay.IsInternal(UrlDisplay.PasswordsUrl));
        Assert.True(UrlDisplay.IsPasswords(UrlDisplay.PasswordsUrl));
        Assert.False(UrlDisplay.IsPasswords("http://passwords.relay/passwords.html"));
        Assert.Null(UrlDisplay.ResolveRelayUrl("relay://nope"));
    }

    [Theory]
    [InlineData("relay://settings", "https://settings.relay/settings.html")]
    [InlineData("relay://settings/privacy", "https://settings.relay/settings.html#privacy")]
    [InlineData("RELAY://Settings/About/", "https://settings.relay/settings.html#about")]
    public void Relay_settings_urls_open_the_settings_page(string input, string expected)
    {
        var r = AddressParser.Parse(input, Engine)!;
        Assert.False(r.IsSearch);
        Assert.Equal(expected, r.Uri.ToString());
    }

    [Fact]
    public void Settings_page_shows_its_relay_address()
    {
        Assert.Equal("relay://settings", UrlDisplay.ForAddressBar(UrlDisplay.SettingsUrl));
        Assert.Equal("relay://settings/blocking", UrlDisplay.ForAddressBar(UrlDisplay.SettingsSectionUrl("blocking")));
        Assert.Equal("relay://settings", UrlDisplay.ForAddressBar(UrlDisplay.SettingsUrl + "#nope"));
        Assert.True(UrlDisplay.IsInternal(UrlDisplay.SettingsUrl));
        Assert.Null(UrlDisplay.ResolveRelayUrl("relay://settings/nope"));
    }
}
