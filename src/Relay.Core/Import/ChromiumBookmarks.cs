using System.Text.Json;
using Relay.Core.Bookmarks;

namespace Relay.Core.Import;

/// <summary>A page from another browser's history.</summary>
public sealed record ImportedVisit(string Url, string Title, DateTime VisitedUtc, int VisitCount);

public static class ImportTime
{
    private static readonly DateTime Min = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Chromium stores times as microseconds since 1601-01-01 UTC (Windows FILETIME / 10).</summary>
    public static DateTime? FromChromium(long micros)
    {
        if (micros <= 0 || micros > DateTime.MaxValue.ToFileTimeUtc() / 10) return null;
        var t = DateTime.FromFileTimeUtc(micros * 10);
        return t < Min ? null : t;
    }

    /// <summary>Firefox stores times as microseconds since the Unix epoch.</summary>
    public static DateTime? FromUnixMicros(long micros)
    {
        if (micros <= 0 || micros / 1000 > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) return null;
        var t = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime;
        return t < Min ? null : t;
    }

    public static bool IsImportableUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.Length <= 8192 &&
        (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the JSON "Bookmarks" file of Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi).</summary>
public static class ChromiumBookmarks
{
    private static readonly (string Key, string Title)[] Roots =
        [("bookmark_bar", "Bookmarks bar"), ("other", "Other bookmarks"), ("synced", "Mobile bookmarks")];

    public static BookmarkNode Parse(string json)
    {
        var root = new BookmarkNode { Title = "Bookmarks" };
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        if (!doc.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object) return root;
        foreach (var (key, title) in Roots)
        {
            if (!roots.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            var folder = new BookmarkNode { Title = title };
            ReadChildren(el, folder, 0);
            if (folder.Children.Count > 0) root.Children.Add(folder);
        }
        return root;
    }

    private static void ReadChildren(JsonElement el, BookmarkNode into, int depth)
    {
        if (depth > 50 || !el.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var c in children.EnumerateArray())
        {
            var type = c.TryGetProperty("type", out var t) ? t.GetString() : null;
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (type == "url")
            {
                var url = c.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (ImportTime.IsImportableUrl(url)) into.Children.Add(new BookmarkNode { Title = name.Length > 0 ? name : url!, Url = url });
            }
            else if (type == "folder")
            {
                var folder = new BookmarkNode { Title = name };
                ReadChildren(c, folder, depth + 1);
                if (folder.Children.Count > 0) into.Children.Add(folder);
            }
        }
    }
}
