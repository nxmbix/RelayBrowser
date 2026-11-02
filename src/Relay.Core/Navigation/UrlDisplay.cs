namespace Relay.Core.Navigation;

/// <summary>Helpers for turning URLs into something friendly to show in the UI.</summary>
public static class UrlDisplay
{
    public const string NewTabHost = "newtab.relay";
    public const string NewTabUrl = "https://newtab.relay/index.html";
    public const string UserAssetsHost = "assets.relay";
    public const string ReaderHost = "reader.relay";
    public const string ReaderUrl = "https://reader.relay/reader.html";

    public static bool IsReader(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsNewTab(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Host.Equals(NewTabHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsInternal(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.Equals(NewTabHost, StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals(UserAssetsHost, StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase));

    /// <summary>Text for the address bar. Internal pages show as empty so the user can type.</summary>
    public static string ForAddressBar(string? url)
    {
        if (string.IsNullOrEmpty(url) || IsNewTab(url) || url == "about:blank") return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? Unescape(u) : url;
    }

    /// <summary>"www.example.com" → "example.com"; empty for non-web URLs.</summary>
    public static string Host(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return string.Empty;
        var h = u.IdnHost.Length > 0 ? u.Host : u.Host;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }

    /// <summary>Short form for lists: host + path without trailing slash or scheme.</summary>
    public static string Short(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url ?? string.Empty;
        if (u.IsFile) return u.LocalPath;
        var s = Host(url) + Uri.UnescapeDataString(u.AbsolutePath);
        return s.EndsWith('/') ? s[..^1] : s;
    }

    public static bool IsSecure(string? url) =>
        url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !IsInternal(url);

    public static bool IsHttp(string? url) =>
        url is not null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string Unescape(Uri u)
    {
        try { return Uri.UnescapeDataString(u.AbsoluteUri); }
        catch { return u.AbsoluteUri; }
    }

    /// <summary>The "origin" (scheme://host[:port]) of a URL, used for permissions.</summary>
    public static string Origin(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return string.Empty;
        return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
