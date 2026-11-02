using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relay.Core.Bookmarks;
using Relay.Core.Import;

namespace Relay.Data;

public enum BrowserFamily { Chromium, Firefox }

/// <summary>A browser profile on this PC that RELAY can copy bookmarks and history from.</summary>
public sealed record BrowserSource(string Browser, string ProfileName, string ProfileDir, BrowserFamily Family)
{
    public string DisplayName => string.IsNullOrEmpty(ProfileName) ? Browser : $"{Browser} — {ProfileName}";
}

/// <summary>
/// Reads bookmarks and history straight from other browsers' profile folders. The other browser's files are
/// never modified: its databases are copied first (so they can be read while it is running).
/// Passwords and cookies are not imported.
/// </summary>
public static class BrowserImport
{
    public static List<BrowserSource> Detect(string? localAppData = null, string? roamingAppData = null)
    {
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<BrowserSource>();
        (string Name, string Dir)[] chromium =
        [
            ("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
            ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
            ("Opera GX", Path.Combine(roaming, "Opera Software", "Opera GX Stable")),
            ("Yandex", Path.Combine(local, "Yandex", "YandexBrowser", "User Data")),
            ("Chromium", Path.Combine(local, "Chromium", "User Data")),
        ];
        foreach (var (name, dir) in chromium)
        {
            try { list.AddRange(DetectChromium(name, dir)); } catch { }
        }
        try { list.AddRange(DetectFirefox(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"))); } catch { }
        return list;
    }

    private static IEnumerable<BrowserSource> DetectChromium(string browser, string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        var names = ProfileNames(Path.Combine(userData, "Local State"));
        var dirs = new List<string> { userData }; // Opera keeps its main profile in the root
        dirs.AddRange(Directory.EnumerateDirectories(userData)
            .Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal) || n.StartsWith("_side_profiles", StringComparison.Ordinal)))
            .OrderBy(d => Path.GetFileName(d) == "Default" ? 0 : 1).ThenBy(d => d, StringComparer.OrdinalIgnoreCase));
        var found = dirs.Where(d => File.Exists(Path.Combine(d, "Bookmarks")) || File.Exists(Path.Combine(d, "History"))).ToList();
        foreach (var d in found)
        {
            var key = Path.GetFileName(d);
            var label = names.TryGetValue(key, out var n) ? n : found.Count > 1 && !string.Equals(d, userData, StringComparison.OrdinalIgnoreCase) ? key : string.Empty;
            yield return new BrowserSource(browser, label, d, BrowserFamily.Chromium);
        }
    }

    private static Dictionary<string, string> ProfileNames(string localState)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(localState)) return map;
            using var doc = JsonDocument.Parse(ReadShared(localState));
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                foreach (var prop in cache.EnumerateObject())
                    if (prop.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) map[prop.Name] = name;
        }
        catch { }
        return map;
    }

    private static IEnumerable<BrowserSource> DetectFirefox(string profiles)
    {
        if (!Directory.Exists(profiles)) yield break;
        foreach (var d in Directory.EnumerateDirectories(profiles).Where(d => File.Exists(Path.Combine(d, "places.sqlite"))))
        {
            var folder = Path.GetFileName(d);
            var dot = folder.IndexOf('.');
            yield return new BrowserSource("Firefox", dot >= 0 ? folder[(dot + 1)..] : folder, d, BrowserFamily.Firefox);
        }
    }

    public static BookmarkNode? ReadBookmarks(BrowserSource source)
    {
        if (source.Family == BrowserFamily.Chromium)
        {
            var file = Path.Combine(source.ProfileDir, "Bookmarks");
            return File.Exists(file) ? ChromiumBookmarks.Parse(ReadShared(file)) : null;
        }
        return WithCopy(Path.Combine(source.ProfileDir, "places.sqlite"), ReadFirefoxBookmarks);
    }

    public static List<ImportedVisit> ReadHistory(BrowserSource source, int limit = 50_000)
    {
        var chromium = source.Family == BrowserFamily.Chromium;
        var db = Path.Combine(source.ProfileDir, chromium ? "History" : "places.sqlite");
        return WithCopy(db, c =>
        {
            var list = new List<ImportedVisit>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = chromium
                ? "SELECT url, title, last_visit_time, visit_count FROM urls WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT $l"
                : "SELECT url, title, last_visit_date, visit_count FROM moz_places WHERE hidden = 0 AND last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.IsDBNull(0) ? null : r.GetString(0);
                if (!ImportTime.IsImportableUrl(url)) continue;
                var micros = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                if ((chromium ? ImportTime.FromChromium(micros) : ImportTime.FromUnixMicros(micros)) is not { } when) continue;
                var title = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                var count = r.IsDBNull(3) ? 1 : Math.Max(1, r.GetInt32(3));
                list.Add(new ImportedVisit(url!, title, when, count));
            }
            return list;
        }) ?? [];
    }

    private static BookmarkNode ReadFirefoxBookmarks(SqliteConnection c)
    {
        var rows = new List<(long Id, int Type, long Parent, string Title, string? Url, string? Guid)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT b.id, b.type, b.parent, IFNULL(b.title, ''), p.url, b.guid
                FROM moz_bookmarks b LEFT JOIN moz_places p ON p.id = b.fk
                ORDER BY b.parent, b.position
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        var byParent = rows.ToLookup(x => x.Parent);
        var root = new BookmarkNode { Title = "Bookmarks" };
        (string Guid, string Title)[] roots = [("toolbar_____", "Bookmarks toolbar"), ("menu________", "Bookmarks menu"), ("unfiled_____", "Other bookmarks"), ("mobile______", "Mobile bookmarks")];
        foreach (var (guid, title) in roots)
        {
            var row = rows.FirstOrDefault(x => x.Guid == guid);
            if (row.Guid is null) continue;
            var folder = new BookmarkNode { Title = title };
            Fill(folder, row.Id, 0);
            if (folder.Children.Count > 0) root.Children.Add(folder);
        }
        return root;

        void Fill(BookmarkNode into, long parent, int depth)
        {
            if (depth > 50) return;
            foreach (var x in byParent[parent])
            {
                if (x.Type == 1 && ImportTime.IsImportableUrl(x.Url))
                    into.Children.Add(new BookmarkNode { Title = x.Title.Length > 0 ? x.Title : x.Url!, Url = x.Url });
                else if (x.Type == 2)
                {
                    var f = new BookmarkNode { Title = x.Title };
                    Fill(f, x.Id, depth + 1);
                    if (f.Children.Count > 0) into.Children.Add(f);
                }
            }
        }
    }

    /// <summary>Copies a (possibly open) SQLite database and its journal to a temp folder, reads it, then deletes the copy.</summary>
    private static T? WithCopy<T>(string dbPath, Func<SqliteConnection, T> read) where T : class
    {
        if (!File.Exists(dbPath)) return null;
        var temp = Path.Combine(Path.GetTempPath(), "relay-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var copy = Path.Combine(temp, "data.db");
            CopyShared(dbPath, copy);
            foreach (var suffix in new[] { "-wal", "-journal" })
                if (File.Exists(dbPath + suffix)) CopyShared(dbPath + suffix, copy + suffix);
            using var c = new SqliteConnection($"Data Source={copy};Pooling=False");
            c.Open();
            return read(c);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        src.CopyTo(dst);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
