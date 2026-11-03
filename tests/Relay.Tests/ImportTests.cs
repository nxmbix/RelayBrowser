using Microsoft.Data.Sqlite;
using Relay.Core.Import;
using Relay.Data;
using Xunit;

namespace Relay.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"relay-import-test-{Guid.NewGuid():N}");

    public ImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private const string ChromeBookmarks = """
        {"roots":{
          "bookmark_bar":{"type":"folder","name":"Bookmarks bar","children":[
            {"type":"url","name":"Example","url":"https://example.com/"},
            {"type":"url","name":"Bad","url":"javascript:alert(1)"},
            {"type":"folder","name":"Dev","children":[{"type":"url","name":"","url":"https://github.com/"}]},
            {"type":"folder","name":"Empty","children":[]}]},
          "other":{"type":"folder","name":"Other","children":[]},
          "synced":{"type":"folder","name":"Mobile","children":[{"type":"url","name":"M","url":"http://m.example/"}]}}}
        """;

    [Fact]
    public void Parses_chromium_bookmarks_and_drops_unsafe_urls()
    {
        var tree = ChromiumBookmarks.Parse(ChromeBookmarks);
        Assert.Equal(["Bookmarks bar", "Mobile bookmarks"], tree.Children.Select(c => c.Title));
        var bar = tree.Children[0];
        Assert.Equal(["Example", "Dev"], bar.Children.Select(c => c.Title));
        Assert.Equal("https://github.com/", bar.Children[1].Children[0].Title);
        Assert.Equal(3, tree.CountBookmarks());
    }

    [Fact]
    public void Converts_browser_timestamps()
    {
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t, ImportTime.FromChromium(t.ToFileTimeUtc() / 10));
        Assert.Equal(t, ImportTime.FromUnixMicros(new DateTimeOffset(t).ToUnixTimeMilliseconds() * 1000));
        Assert.Null(ImportTime.FromChromium(0));
        Assert.Null(ImportTime.FromUnixMicros(-5));
    }

    [Fact]
    public void Detects_chromium_profiles_and_reads_history()
    {
        var local = Path.Combine(_root, "local");
        var profile = Path.Combine(local, "Google", "Chrome", "User Data", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "..", "Local State"), """{"profile":{"info_cache":{"Default":{"name":"Nik"}}}}""");
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), ChromeBookmarks);
        var when = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(profile, "History")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, last_visit_time INTEGER, hidden INTEGER);
                INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES
                  ('https://a.example/', 'A', 3, {when.ToFileTimeUtc() / 10}, 0),
                  ('chrome://settings', 'S', 1, {when.ToFileTimeUtc() / 10}, 0),
                  ('https://hidden.example/', 'H', 1, {when.ToFileTimeUtc() / 10}, 1);
                """;
            cmd.ExecuteNonQuery();
        }

        var source = Assert.Single(BrowserImport.Detect(local, Path.Combine(_root, "roaming")));
        Assert.Equal("Google Chrome — Nik", source.DisplayName);
        Assert.Equal(3, BrowserImport.ReadBookmarks(source)!.CountBookmarks());
        var visit = Assert.Single(BrowserImport.ReadHistory(source));
        Assert.Equal(("https://a.example/", "A", when, 3), (visit.Url, visit.Title, visit.VisitedUtc, visit.VisitCount));
    }

    [Fact]
    public void Reads_firefox_bookmarks_and_history()
    {
        var roaming = Path.Combine(_root, "roaming");
        var profile = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles", "abc123.default-release");
        Directory.CreateDirectory(profile);
        var when = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var micros = new DateTimeOffset(when).ToUnixTimeMilliseconds() * 1000;
        using (var c = new SqliteConnection($"Data Source={Path.Combine(profile, "places.sqlite")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, hidden INTEGER, last_visit_date INTEGER);
                CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, parent INTEGER, position INTEGER, title TEXT, guid TEXT);
                INSERT INTO moz_places VALUES (1, 'https://mozilla.org/', 'Mozilla', 5, 0, {micros}), (2, 'place:sort=8', NULL, 0, 0, NULL);
                INSERT INTO moz_bookmarks VALUES (1, 2, NULL, 0, 0, '', 'root________'),
                  (2, 2, NULL, 1, 0, 'toolbar', 'toolbar_____'),
                  (3, 1, 1, 2, 0, 'Mozilla', 'b1'),
                  (4, 1, 2, 2, 1, 'Smart', 'b2');
                """;
            cmd.ExecuteNonQuery();
        }

        var source = Assert.Single(BrowserImport.Detect(Path.Combine(_root, "local"), roaming));
        Assert.Equal("Firefox — default-release", source.DisplayName);
        var tree = BrowserImport.ReadBookmarks(source)!;
        Assert.Equal("Bookmarks toolbar", Assert.Single(tree.Children).Title);
        Assert.Equal(1, tree.CountBookmarks());
        var visit = Assert.Single(BrowserImport.ReadHistory(source));
        Assert.Equal(("https://mozilla.org/", when, 5), (visit.Url, visit.VisitedUtc, visit.VisitCount));
    }

    [Fact]
    public async Task Merges_imported_history()
    {
        var db = new RelayDatabase(Path.Combine(_root, "relay.db"));
        db.Migrate();
        var history = new HistoryRepository(db);
        await history.RecordVisitAsync("https://a.example/", "Mine");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var n = await history.ImportAsync([new("https://a.example/", "Theirs", old, 9), new("https://b.example/", "B", old, 2)]);
        Assert.Equal(2, n);
        var all = await history.GetRecentAsync();
        var a = all.Single(h => h.Url == "https://a.example/");
        Assert.Equal("Mine", a.Title);
        Assert.Equal(9, a.VisitCount);
        Assert.True(a.VisitedAt > old);
        Assert.Contains(all, h => h.Url == "https://b.example/");
    }
}

public class CommandLineArgsTests
{
    [Fact]
    public void Single_argument_blocks_switch_injection()
    {
        var raw = "\"C:\\RELAY\\Relay.exe\" --single-argument https://x/\" --uninstall --quiet \"";
        var args = Relay.Core.Navigation.CommandLineArgs.Normalize(["--single-argument", "https://x/", "--uninstall", "--quiet", ""], raw);
        Assert.Equal(["https://x/\" --uninstall --quiet \""], args);
        Assert.DoesNotContain(args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plain_arguments_pass_through()
    {
        string[] a = ["--uninstall"];
        Assert.Same(a, Relay.Core.Navigation.CommandLineArgs.Normalize(a, "Relay.exe --uninstall"));
        Assert.Empty(Relay.Core.Navigation.CommandLineArgs.Normalize(["--single-argument"], "Relay.exe --single-argument"));
        Assert.Empty(Relay.Core.Navigation.CommandLineArgs.Normalize(["--single-argument", "--uninstall"], "Relay.exe --single-argument --uninstall"));
    }
}
