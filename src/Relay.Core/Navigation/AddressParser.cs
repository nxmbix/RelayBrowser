using System.Net;
using System.Text.RegularExpressions;
using Relay.Core.Search;

namespace Relay.Core.Navigation;

/// <summary>The outcome of interpreting what the user typed into the address bar.</summary>
public sealed record AddressResult(Uri Uri, bool IsSearch);

/// <summary>
/// Decides whether address-bar input is a URL to navigate to or a query to search for.
/// Pure logic with no UI dependencies so it can be unit-tested.
/// </summary>
public static partial class AddressParser
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "about", "view-source", "edge", "mailto"
    };

    // Schemes that must never be navigated to from typed input.
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data"
    };

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^[a-z]{2,63}$|^xn--[a-z0-9-]{1,59}$", RegexOptions.IgnoreCase)]
    private static partial Regex TldRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    /// <summary>
    /// Interprets <paramref name="input"/>. Returns null when the input is empty.
    /// </summary>
    public static AddressResult? Parse(string? input, SearchEngine engine)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        // Explicit "?query" forces a search, like other browsers.
        if (text.StartsWith('?'))
        {
            var q = text[1..].Trim();
            return q.Length == 0 ? null : new AddressResult(engine.BuildSearchUri(q), true);
        }

        if (LooksLikeUrl(text, out var uri)) return new AddressResult(uri!, false);
        return new AddressResult(engine.BuildSearchUri(text), true);
    }

    /// <summary>True when the text should be navigated to directly.</summary>
    public static bool LooksLikeUrl(string text, out Uri? uri)
    {
        uri = null;
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace) && !HasExplicitScheme(text)) return false;

        // Windows paths: C:\folder\file.html
        if (text.Length > 2 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            {
                uri = fileUri;
                return true;
            }
            return false;
        }

        if (HasExplicitScheme(text))
        {
            var scheme = text[..text.IndexOf(':')];
            if (BlockedSchemes.Contains(scheme)) return false;

            // "localhost:3000" and "example.com:8080" are not schemes.
            if (!AllowedSchemes.Contains(scheme))
            {
                return TryHostLike(text, out uri);
            }

            if (text.Contains(' ') && !scheme.Equals("view-source", StringComparison.OrdinalIgnoreCase))
                text = text.Replace(" ", "%20");

            if (Uri.TryCreate(text, UriKind.Absolute, out var abs))
            {
                // http:// and https:// need a host.
                if ((abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(abs.Host))
                    return false;
                uri = abs;
                return true;
            }
            return false;
        }

        return TryHostLike(text, out uri);
    }

    private static bool HasExplicitScheme(string text)
    {
        var m = SchemeRegex().Match(text);
        if (!m.Success) return false;
        // "localhost:8080" / "example.com:443/path": text after ':' starts with a digit => port, not scheme.
        var after = text[m.Length..];
        if (after.Length > 0 && char.IsDigit(after[0])) return false;
        return true;
    }

    private static bool TryHostLike(string text, out Uri? uri)
    {
        uri = null;
        // Split off path/query/fragment to inspect the authority.
        var cut = text.IndexOfAny(['/', '?', '#']);
        var authority = cut >= 0 ? text[..cut] : text;
        if (authority.Length == 0) return false;
        if (authority.Contains('@')) authority = authority[(authority.LastIndexOf('@') + 1)..];

        string host = authority;
        string? port = null;

        if (host.StartsWith('['))
        {
            // IPv6 literal: [::1]:8080
            var end = host.IndexOf(']');
            if (end < 0) return false;
            var ip = host[1..end];
            if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            return Build("http://" + text, out uri);
        }

        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            port = host[(colon + 1)..];
            host = host[..colon];
            if (port.Length == 0 || port.Length > 5 || !port.All(char.IsDigit) || int.Parse(port) > 65535) return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return Build("http://" + text, out uri);

        if (IsIPv4(host))
            return Build("http://" + text, out uri);

        if (!host.Contains('.')) return false;
        if (host.EndsWith('.')) host = host[..^1];

        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        if (labels.Any(l => !LabelRegex().IsMatch(IdnSafe(l)))) return false;
        if (!TldRegex().IsMatch(IdnSafe(labels[^1]))) return false;

        return Build("https://" + text, out uri);
    }

    private static string IdnSafe(string label)
    {
        if (label.All(c => c < 128)) return label;
        try { return new System.Globalization.IdnMapping().GetAscii(label); }
        catch (ArgumentException) { return "-invalid-"; }
    }

    private static bool IsIPv4(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Length > 3 || !p.All(char.IsDigit)) return false;
            if (int.Parse(p) > 255) return false;
        }
        return true;
    }

    private static bool Build(string candidate, out Uri? uri)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
        {
            uri = u;
            return true;
        }
        uri = null;
        return false;
    }
}
