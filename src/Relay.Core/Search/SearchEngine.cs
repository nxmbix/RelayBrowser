namespace Relay.Core.Search;

/// <summary>A search provider. <c>%s</c> in templates is replaced by the URL-encoded query.</summary>
public sealed record SearchEngine(string Id, string Name, string SearchTemplate, string? SuggestTemplate, SuggestFormat SuggestFormat)
{
    public Uri BuildSearchUri(string query) =>
        new(SearchTemplate.Replace("%s", Uri.EscapeDataString(query.Trim())));

    public Uri? BuildSuggestUri(string query) =>
        SuggestTemplate is null ? null : new Uri(SuggestTemplate.Replace("%s", Uri.EscapeDataString(query)));

    public static bool IsValidTemplate(string? template) =>
        !string.IsNullOrWhiteSpace(template) && template.Contains("%s") &&
        Uri.TryCreate(template.Replace("%s", "test"), UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

public enum SuggestFormat
{
    None,
    /// <summary>OpenSearch JSON: ["query", ["s1","s2"]].</summary>
    OpenSearch,
    /// <summary>DuckDuckGo: [{"phrase":"s1"}].</summary>
    DuckDuckGo,
}

public static class SearchEngines
{
    public static readonly SearchEngine Google = new("google", "Google",
        "https://www.google.com/search?q=%s",
        "https://suggestqueries.google.com/complete/search?client=firefox&q=%s", SuggestFormat.OpenSearch);

    public static readonly SearchEngine Bing = new("bing", "Bing",
        "https://www.bing.com/search?q=%s",
        "https://api.bing.com/osjson.aspx?query=%s", SuggestFormat.OpenSearch);

    public static readonly SearchEngine DuckDuckGo = new("duckduckgo", "DuckDuckGo",
        "https://duckduckgo.com/?q=%s",
        "https://duckduckgo.com/ac/?q=%s&type=list", SuggestFormat.OpenSearch);

    public static readonly SearchEngine Brave = new("brave", "Brave Search",
        "https://search.brave.com/search?q=%s",
        "https://search.brave.com/api/suggest?q=%s", SuggestFormat.OpenSearch);

    public static readonly SearchEngine Ecosia = new("ecosia", "Ecosia",
        "https://www.ecosia.org/search?q=%s",
        "https://ac.ecosia.org/autocomplete?q=%s&type=list", SuggestFormat.OpenSearch);

    public static readonly SearchEngine Startpage = new("startpage", "Startpage",
        "https://www.startpage.com/do/search?q=%s", null, SuggestFormat.None);

    public static readonly SearchEngine Yahoo = new("yahoo", "Yahoo",
        "https://search.yahoo.com/search?p=%s", null, SuggestFormat.None);

    public static IReadOnlyList<SearchEngine> BuiltIn { get; } = [Google, Brave, Bing, Yahoo, DuckDuckGo, Ecosia, Startpage];

    /// <summary>One-line, neutral descriptions shown on the first-run choice screen.</summary>
    public static string Describe(string id) => id switch
    {
        "google" => "The most widely used search engine.",
        "brave" => "Private search built on its own independent index.",
        "bing" => "Microsoft's search engine.",
        "yahoo" => "Search alongside Yahoo's news, finance and mail.",
        "duckduckgo" => "Doesn't build a profile from your searches.",
        "ecosia" => "Uses its advertising income to plant trees.",
        "startpage" => "Google results with your identity removed.",
        _ => string.Empty,
    };

    public const string CustomId = "custom";

    public static SearchEngine Resolve(string? id, string? customTemplate)
    {
        if (id == CustomId && SearchEngine.IsValidTemplate(customTemplate))
            return new SearchEngine(CustomId, "Custom", customTemplate!, null, SuggestFormat.None);
        return BuiltIn.FirstOrDefault(e => e.Id == id) ?? Google;
    }
}
