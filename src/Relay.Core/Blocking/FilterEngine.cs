using System.Text;

namespace Relay.Core.Blocking;

/// <summary>What kind of resource a request loads (mirrors WebView2's resource contexts).</summary>
public enum RequestType
{
    Other, Document, Subdocument, Script, Image, Stylesheet, Font, Media, XmlHttpRequest, WebSocket, Ping,
}

[Flags]
internal enum TypeMask
{
    None = 0,
    Subdocument = 1 << 1, Script = 1 << 2, Image = 1 << 3, Stylesheet = 1 << 4, Font = 1 << 5,
    Media = 1 << 6, XmlHttpRequest = 1 << 7, WebSocket = 1 << 8, Ping = 1 << 9, Other = 1 << 10, Document = 1 << 11,
    AllButDocument = Subdocument | Script | Image | Stylesheet | Font | Media | XmlHttpRequest | WebSocket | Ping | Other,
}

/// <summary>
/// A compact ad/tracker filter engine for the Adblock Plus / EasyList syntax.
/// Supports network rules (<c>||host^</c>, wildcards <c>*</c>, separators <c>^</c>, anchors <c>|</c>),
/// exceptions (<c>@@</c>), the common options (<c>third-party</c>, resource types, <c>domain=</c>,
/// <c>document</c> exceptions) and domain-specific element hiding (<c>example.com##.ad</c>).
/// Rules with options it doesn't understand are skipped rather than guessed at, so it errs on the side of not breaking pages.
/// </summary>
public sealed class FilterEngine
{
    private sealed class Rule
    {
        public required string Text;
        public string Pattern = string.Empty;   // lower-case, without anchors
        public bool HostAnchor;                 // ||
        public bool StartAnchor;                // |...
        public bool EndAnchor;                  // ...|
        public TypeMask Types = TypeMask.AllButDocument;
        public bool? ThirdParty;                // null = either
        public string[]? IncludeDomains;
        public string[]? ExcludeDomains;
        public bool MatchCase;
        public string Required = string.Empty;  // longest literal run; a URL without it can't match
    }

    // ||example.com^ style rules, keyed by host. The vast majority of real-world lists.
    private readonly Dictionary<string, List<Rule>> _hostBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Rule>> _hostExceptions = new(StringComparer.Ordinal);
    // Everything else, indexed by a rare-ish token taken from the pattern.
    private readonly Dictionary<string, List<Rule>> _tokenBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Rule>> _tokenExceptions = new(StringComparer.Ordinal);
    private readonly List<Rule> _untokenizedBlocks = [];
    private readonly List<Rule> _untokenizedExceptions = [];
    // example.com##selector
    private readonly Dictionary<string, List<string>> _cosmetic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _cosmeticExceptions = new(StringComparer.Ordinal);

    public int NetworkRuleCount { get; private set; }
    public int CosmeticRuleCount { get; private set; }

    // ------------------------------------------------------------------ Parsing

    public void AddRules(IEnumerable<string> lines)
    {
        foreach (var raw in lines) AddRule(raw);
    }

    public void AddRules(string text) => AddRules(text.Split('\n'));

    public bool AddRule(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == '[') return false;

        var hashIdx = line.IndexOf('#');
        if (hashIdx >= 0 && hashIdx + 1 < line.Length &&
            (line.AsSpan(hashIdx).StartsWith("##") || line.AsSpan(hashIdx).StartsWith("#@#") ||
             line.AsSpan(hashIdx).StartsWith("#?#") || line.AsSpan(hashIdx).StartsWith("#$#") || line.AsSpan(hashIdx).StartsWith("#%#")))
            return AddCosmetic(line, hashIdx);

        return AddNetwork(line);
    }

    private bool AddCosmetic(string line, int hashIdx)
    {
        var isException = line.AsSpan(hashIdx).StartsWith("#@#");
        if (!isException && !line.AsSpan(hashIdx).StartsWith("##")) return false; // extended/scriptlet syntaxes aren't supported
        var selector = line[(hashIdx + (isException ? 3 : 2))..].Trim();
        if (selector.Length == 0 || selector.StartsWith("+js") || selector.Contains(":-abp-") || selector.Contains(":has-text") ||
            selector.Contains(":xpath") || selector.Contains(":style(") || selector.Contains(":remove(") || selector.Contains(":matches-") ||
            selector.Contains('{') || selector.Contains('}') || selector.Length > 500)
            return false;

        var domains = line[..hashIdx].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !d.StartsWith('~')).Select(d => d.ToLowerInvariant()).ToList();
        // Generic (domain-less) element hiding is skipped: injecting tens of thousands of selectors
        // into every page costs more than it saves.
        if (domains.Count == 0) return false;

        foreach (var d in domains)
        {
            if (isException)
            {
                if (!_cosmeticExceptions.TryGetValue(d, out var set)) _cosmeticExceptions[d] = set = [];
                set.Add(selector);
            }
            else
            {
                if (!_cosmetic.TryGetValue(d, out var list)) _cosmetic[d] = list = [];
                list.Add(selector);
            }
        }
        CosmeticRuleCount++;
        return true;
    }

    private bool AddNetwork(string line)
    {
        var isException = line.StartsWith("@@");
        if (isException) line = line[2..];
        var rule = new Rule { Text = line };
        var pattern = line;
        var dollar = line.LastIndexOf('$');
        if (dollar >= 0 && dollar < line.Length - 1 && LooksLikeOptions(line.AsSpan(dollar + 1)))
        {
            if (!ParseOptions(rule, line[(dollar + 1)..], isException)) return false;
            pattern = line[..dollar];
        }

        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/') return false; // regular-expression rules are skipped
        if (pattern.StartsWith("||")) { rule.HostAnchor = true; pattern = pattern[2..]; }
        else if (pattern.StartsWith('|')) { rule.StartAnchor = true; pattern = pattern[1..]; }
        if (pattern.EndsWith('|')) { rule.EndAnchor = true; pattern = pattern[..^1]; }
        while (pattern.StartsWith('*') && !rule.HostAnchor && !rule.StartAnchor) pattern = pattern[1..];
        while (pattern.EndsWith('*') && !rule.EndAnchor) pattern = pattern[..^1];
        if (!rule.MatchCase) pattern = pattern.ToLowerInvariant();
        rule.Pattern = pattern;
        rule.Required = pattern.Split('*', '^').OrderByDescending(x => x.Length).FirstOrDefault() ?? string.Empty;

        if (pattern.Length == 0 && !rule.HostAnchor)
        {
            // An option-only rule such as "$third-party,domain=x" — too broad for a general engine.
            if (rule.IncludeDomains is null) return false;
        }
        if (pattern.Length > 0 && pattern.Length < 3 && !rule.HostAnchor && rule.IncludeDomains is null) return false;

        // Pure host rules: ||ads.example.com^ (optionally ^|)
        if (rule.HostAnchor && IsPureHost(pattern, out var host))
        {
            Add(isException ? _hostExceptions : _hostBlocks, host, rule);
        }
        else
        {
            var token = BestToken(pattern, rule.HostAnchor || rule.StartAnchor, rule.EndAnchor);
            if (token is null) (isException ? _untokenizedExceptions : _untokenizedBlocks).Add(rule);
            else Add(isException ? _tokenExceptions : _tokenBlocks, token, rule);
        }
        NetworkRuleCount++;
        return true;
    }

    private static bool LooksLikeOptions(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c is ',' or '=' or '~' or '|' or '-' or '_' or '.' or '*')) return false;
        return true;
    }

    private static bool ParseOptions(Rule rule, string options, bool isException)
    {
        TypeMask include = TypeMask.None, exclude = TypeMask.None;
        foreach (var rawOpt in options.Split(','))
        {
            var opt = rawOpt.Trim().ToLowerInvariant();
            var neg = opt.StartsWith('~');
            if (neg) opt = opt[1..];
            TypeMask type = opt switch
            {
                "script" => TypeMask.Script,
                "image" => TypeMask.Image,
                "stylesheet" or "css" => TypeMask.Stylesheet,
                "font" => TypeMask.Font,
                "media" => TypeMask.Media,
                "xmlhttprequest" or "xhr" => TypeMask.XmlHttpRequest,
                "subdocument" or "frame" => TypeMask.Subdocument,
                "websocket" => TypeMask.WebSocket,
                "ping" or "beacon" => TypeMask.Ping,
                "other" or "object" or "object-subrequest" => TypeMask.Other,
                "document" or "doc" => TypeMask.Document,
                _ => TypeMask.None,
            };
            if (type != TypeMask.None)
            {
                if (neg) exclude |= type; else include |= type;
                continue;
            }
            switch (opt)
            {
                case "third-party" or "3p": rule.ThirdParty = !neg; break;
                case "first-party" or "1p": rule.ThirdParty = neg; break;
                case "match-case": rule.MatchCase = true; break;
                case "important" or "all": break;
                default:
                    if (opt.StartsWith("domain=") || opt.StartsWith("from="))
                    {
                        var list = opt[(opt.IndexOf('=') + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries);
                        var inc = list.Where(d => !d.StartsWith('~')).ToArray();
                        var exc = list.Where(d => d.StartsWith('~')).Select(d => d[1..]).ToArray();
                        if (inc.Any(d => d.Contains('*')) || exc.Any(d => d.Contains('*'))) return false; // entity wildcards unsupported
                        rule.IncludeDomains = inc.Length > 0 ? inc : null;
                        rule.ExcludeDomains = exc.Length > 0 ? exc : null;
                        break;
                    }
                    // redirect, csp, removeparam, popup, elemhide, badfilter, … — not supported: skip the rule.
                    return false;
            }
        }
        if (include != TypeMask.None) rule.Types = include;
        else if (exclude != TypeMask.None) rule.Types = TypeMask.AllButDocument & ~exclude;
        // "document" blocking rules would block whole pages; only honour it on exceptions (allow-listing a page).
        if (!isException && rule.Types == TypeMask.Document) return false;
        return true;
    }

    private static bool IsPureHost(string pattern, out string host)
    {
        host = pattern;
        if (host.EndsWith("^")) host = host[..^1];
        if (host.Length == 0) return false;
        foreach (var c in host)
            if (!(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        return host.Contains('.');
    }

    /// <summary>
    /// Picks the longest alphanumeric run in the pattern that is guaranteed to appear as a whole token
    /// in any matching URL (bounded by separators or anchors, never by a wildcard).
    /// </summary>
    private static string? BestToken(string pattern, bool anchoredStart, bool anchoredEnd)
    {
        string? best = null;
        var i = 0;
        while (i < pattern.Length)
        {
            if (!char.IsLetterOrDigit(pattern[i])) { i++; continue; }
            var start = i;
            while (i < pattern.Length && char.IsLetterOrDigit(pattern[i])) i++;
            var leftOk = start == 0 ? anchoredStart : pattern[start - 1] != '*';
            var rightOk = i == pattern.Length ? anchoredEnd : pattern[i] != '*';
            if (!leftOk || !rightOk || i - start < 3) continue;
            var tok = pattern[start..i];
            if (tok is "http" or "https" or "www" or "com" or "net" or "org") continue;
            if (best is null || tok.Length > best.Length) best = tok;
        }
        return best;
    }

    private static void Add(Dictionary<string, List<Rule>> d, string key, Rule r)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = [];
        list.Add(r);
    }

    // ------------------------------------------------------------------ Matching

    public readonly record struct MatchResult(bool Blocked, string? Rule);

    /// <summary>Decides whether a request should be blocked.</summary>
    /// <param name="url">Request URL.</param>
    /// <param name="pageHost">Host of the top-level page making the request.</param>
    public MatchResult Match(Uri url, string pageHost, RequestType type)
    {
        if (url.Scheme is not ("http" or "https" or "ws" or "wss")) return default;
        var host = url.IdnHost.ToLowerInvariant();
        pageHost = pageHost.ToLowerInvariant();
        var urlText = url.AbsoluteUri;
        var lower = urlText.ToLowerInvariant();
        var thirdParty = !string.Equals(BaseDomain(host), BaseDomain(pageHost), StringComparison.Ordinal);
        var mask = ToMask(type);

        var block = FindHost(_hostBlocks, host, pageHost, thirdParty, mask)
                    ?? FindToken(_tokenBlocks, _untokenizedBlocks, urlText, lower, host, pageHost, thirdParty, mask);
        if (block is null) return default;

        var exception = FindHost(_hostExceptions, host, pageHost, thirdParty, mask)
                        ?? FindToken(_tokenExceptions, _untokenizedExceptions, urlText, lower, host, pageHost, thirdParty, mask);
        return exception is null ? new MatchResult(true, block.Text) : default;
    }

    /// <summary>True when an <c>@@...$document</c> exception allow-lists the whole page.</summary>
    public bool IsPageAllowed(Uri pageUrl)
    {
        var host = pageUrl.IdnHost.ToLowerInvariant();
        var lower = pageUrl.AbsoluteUri.ToLowerInvariant();
        return FindHost(_hostExceptions, host, host, false, TypeMask.Document) is not null ||
               FindToken(_tokenExceptions, _untokenizedExceptions, pageUrl.AbsoluteUri, lower, host, host, false, TypeMask.Document) is not null;
    }

    private static TypeMask ToMask(RequestType t) => t switch
    {
        RequestType.Document => TypeMask.Document,
        RequestType.Subdocument => TypeMask.Subdocument,
        RequestType.Script => TypeMask.Script,
        RequestType.Image => TypeMask.Image,
        RequestType.Stylesheet => TypeMask.Stylesheet,
        RequestType.Font => TypeMask.Font,
        RequestType.Media => TypeMask.Media,
        RequestType.XmlHttpRequest => TypeMask.XmlHttpRequest,
        RequestType.WebSocket => TypeMask.WebSocket,
        RequestType.Ping => TypeMask.Ping,
        _ => TypeMask.Other,
    };

    private static Rule? FindHost(Dictionary<string, List<Rule>> map, string host, string pageHost, bool thirdParty, TypeMask mask)
    {
        if (map.Count == 0) return null;
        var h = host;
        while (true)
        {
            if (map.TryGetValue(h, out var rules))
                foreach (var r in rules)
                    if (OptionsMatch(r, pageHost, thirdParty, mask)) return r;
            var dot = h.IndexOf('.');
            if (dot < 0 || dot == h.Length - 1) return null;
            h = h[(dot + 1)..];
            if (!h.Contains('.')) return null; // don't match bare TLDs
        }
    }

    private Rule? FindToken(Dictionary<string, List<Rule>> map, List<Rule> untokenized, string url, string lower, string host,
        string pageHost, bool thirdParty, TypeMask mask)
    {
        var i = 0;
        while (i < lower.Length)
        {
            if (!char.IsLetterOrDigit(lower[i])) { i++; continue; }
            var start = i;
            while (i < lower.Length && char.IsLetterOrDigit(lower[i])) i++;
            if (i - start < 3) continue;
            if (map.TryGetValue(lower[start..i], out var rules))
                foreach (var r in rules)
                    if (OptionsMatch(r, pageHost, thirdParty, mask) && PatternMatches(r, r.MatchCase ? url : lower, host)) return r;
        }
        foreach (var r in untokenized)
            if (OptionsMatch(r, pageHost, thirdParty, mask) && PatternMatches(r, r.MatchCase ? url : lower, host)) return r;
        return null;
    }

    private static bool OptionsMatch(Rule r, string pageHost, bool thirdParty, TypeMask mask)
    {
        if ((r.Types & mask) == 0) return false;
        if (r.ThirdParty is bool tp && tp != thirdParty) return false;
        if (r.ExcludeDomains is not null && r.ExcludeDomains.Any(d => HostMatches(pageHost, d))) return false;
        if (r.IncludeDomains is not null && !r.IncludeDomains.Any(d => HostMatches(pageHost, d))) return false;
        return true;
    }

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static bool PatternMatches(Rule r, string url, string host)
    {
        var p = r.Pattern;
        if (p.Length == 0) return true;
        if (r.Required.Length > 0 && !url.Contains(r.Required, StringComparison.Ordinal)) return false;
        if (r.HostAnchor)
        {
            // The pattern must start at the beginning of the host or right after a '.' in it.
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return false;
            var hostStart = schemeEnd + 3;
            var at = url.IndexOf('@', hostStart);
            var slash = url.IndexOf('/', hostStart);
            if (at > 0 && (slash < 0 || at < slash)) hostStart = at + 1;
            var hostEnd = hostStart + host.Length;
            for (var pos = hostStart; pos < hostEnd && pos <= url.Length; pos++)
            {
                if (pos == hostStart || url[pos - 1] == '.')
                    if (MatchAt(p, 0, url, pos, r.EndAnchor)) return true;
            }
            return false;
        }
        if (r.StartAnchor) return MatchAt(p, 0, url, 0, r.EndAnchor);
        for (var pos = 0; pos < url.Length; pos++)
            if (MatchAt(p, 0, url, pos, r.EndAnchor)) return true;
        return false;
    }

    /// <summary>Glob match supporting '*' (any run) and '^' (separator or end of URL).</summary>
    private static bool MatchAt(string p, int pi, string s, int si, bool endAnchor)
    {
        while (pi < p.Length)
        {
            var c = p[pi];
            if (c == '*')
            {
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi == p.Length) return true;
                for (var k = si; k <= s.Length; k++)
                    if (MatchAt(p, pi, s, k, endAnchor)) return true;
                return false;
            }
            if (c == '^')
            {
                if (si == s.Length) { pi++; continue; } // end of address counts as a separator
                if (!IsSeparator(s[si])) return false;
                pi++; si++;
                continue;
            }
            if (si >= s.Length || s[si] != c) return false;
            pi++; si++;
        }
        return !endAnchor || si == s.Length;
    }

    private static bool IsSeparator(char c) =>
        !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '%');

    // ------------------------------------------------------------------ Cosmetic

    /// <summary>Element-hiding selectors that apply to <paramref name="host"/>.</summary>
    public IReadOnlyList<string> SelectorsFor(string host)
    {
        host = host.ToLowerInvariant();
        var result = new List<string>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var h = host;
        while (h.Length > 0)
        {
            if (_cosmeticExceptions.TryGetValue(h, out var ex)) excluded.UnionWith(ex);
            if (_cosmetic.TryGetValue(h, out var list)) result.AddRange(list);
            var dot = h.IndexOf('.');
            if (dot < 0) break;
            h = h[(dot + 1)..];
        }
        return result.Where(s => !excluded.Contains(s)).Distinct().Take(2000).ToList();
    }

    /// <summary>A stylesheet hiding the given selectors. One rule per selector so an invalid one can't disable the rest.</summary>
    public static string BuildStylesheet(IEnumerable<string> selectors)
    {
        var sb = new StringBuilder();
        foreach (var s in selectors) sb.Append(s).Append("{display:none!important}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Helpers

    private static readonly HashSet<string> SecondLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "co.jp", "ne.jp", "or.jp", "com.au", "net.au", "org.au", "co.nz", "com.br",
        "com.cn", "com.tw", "com.hk", "co.kr", "co.in", "com.mx", "com.ar", "com.tr", "co.za", "com.sg", "github.io", "blogspot.com",
    };

    /// <summary>Approximate registrable domain ("news.bbc.co.uk" → "bbc.co.uk").</summary>
    public static string BaseDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return string.Empty;
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var lastTwo = parts[^2] + "." + parts[^1];
        return SecondLevelSuffixes.Contains(lastTwo) ? parts[^3] + "." + lastTwo : lastTwo;
    }
}
