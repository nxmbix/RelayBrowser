using Relay.Core.Bookmarks;
using Relay.Core.Downloads;
using Relay.Core.Navigation;
using Relay.Core.Search;
using Relay.Core.Settings;
using Xunit;

namespace Relay.Tests;

public class FileNameSafetyTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\evil.dll", "evil.dll")]
    [InlineData("a<b>c:d|e?.txt", "a_b_c_d_e_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("trailing. . .", "trailing")]
    [InlineData("", "download")]
    [InlineData("...", "download")]
    [InlineData(".hidden", "hidden")]
    [InlineData("invoice‮txt.exe", "invoicetxt.exe")]
    public void Sanitizes(string input, string expected) => Assert.Equal(expected, FileNameSafety.Sanitize(input));

    [Fact]
    public void Truncates_long_names_keeping_extension()
    {
        var name = new string('a', 400) + ".zip";
        var s = FileNameSafety.Sanitize(name);
        Assert.True(s.Length <= FileNameSafety.MaxNameLength);
        Assert.EndsWith(".zip", s);
    }

    [Fact]
    public void Unique_path_adds_counter()
    {
        var existing = new HashSet<string> { Path.Combine("d", "file.zip"), Path.Combine("d", "file (1).zip") };
        var p = FileNameSafety.UniquePath("d", "file.zip", existing.Contains);
        Assert.Equal(Path.Combine("d", "file (2).zip"), p);
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("script.PS1", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.txt", false)]
    public void Flags_executables(string name, bool dangerous) => Assert.Equal(dangerous, FileNameSafety.IsDangerous(name));

    [Fact]
    public void Detects_path_escape()
    {
        var dir = Path.Combine(Path.GetTempPath(), "relay-dl");
        Assert.True(FileNameSafety.IsInside(dir, Path.Combine(dir, "a.txt")));
        Assert.False(FileNameSafety.IsInside(dir, Path.Combine(dir, "..", "a.txt")));
    }

    [Fact]
    public void Formats_bytes()
    {
        Assert.Equal("512 B", FileNameSafety.FormatBytes(512));
        Assert.Equal("1.5 KB", FileNameSafety.FormatBytes(1536));
        Assert.Equal("2 MB", FileNameSafety.FormatBytes(2 * 1024 * 1024));
    }
}

public class SuggestionParserTests
{
    [Fact]
    public void Parses_opensearch()
    {
        var r = SuggestionParser.Parse("""["cat",["cats","cat food","Cats"]]""", SuggestFormat.OpenSearch);
        Assert.Equal(["cats", "cat food"], r);
    }

    [Fact]
    public void Parses_duckduckgo()
    {
        var r = SuggestionParser.Parse("""[{"phrase":"dogs"},{"phrase":"dog breeds"}]""", SuggestFormat.DuckDuckGo);
        Assert.Equal(["dogs", "dog breeds"], r);
    }

    [Fact]
    public void Survives_garbage() => Assert.Empty(SuggestionParser.Parse("<html>", SuggestFormat.OpenSearch));
}

public class BookmarkFormatTests
{
    private const string ChromeExport = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
            <DL><p>
                <DT><A HREF="https://example.com/" ADD_DATE="1" ICON="data:image/png;base64,AAA">Example &amp; Co</A>
                <DT><H3>Empty</H3>
                <DT><H3>Dev</H3>
                <DL><p>
                    <DT><A HREF="https://github.com/">GitHub</A>
                    <DT><A HREF="javascript:alert(1)">Bookmarklet</A>
                </DL><p>
            </DL><p>
            <DT><A HREF="https://news.example.org/">News</A>
        </DL><p>
        """;

    [Fact]
    public void Parses_chrome_export()
    {
        var root = BookmarkFormatTestsHelper.Parse(ChromeExport);
        Assert.Equal(2, root.Children.Count);
        var bar = root.Children[0];
        Assert.Equal("Bookmarks bar", bar.Title);
        Assert.Equal("Example & Co", bar.Children[0].Title);
        Assert.Equal("Empty", bar.Children[1].Title);
        Assert.True(bar.Children[1].IsFolder);
        Assert.Empty(bar.Children[1].Children);
        var dev = bar.Children[2];
        Assert.Single(dev.Children); // javascript: is skipped
        Assert.Equal("https://news.example.org/", root.Children[1].Url);
        Assert.Equal(3, root.CountBookmarks());
    }

    [Fact]
    public void Round_trips()
    {
        var root = NetscapeBookmarks.Parse(ChromeExport);
        var again = NetscapeBookmarks.Parse(NetscapeBookmarks.Export(root));
        Assert.Equal(root.CountBookmarks(), again.CountBookmarks());
        Assert.Equal("Example & Co", again.Children[0].Children[0].Title);
    }
}

internal static class BookmarkFormatTestsHelper
{
    public static BookmarkNode Parse(string s) => NetscapeBookmarks.Parse(s);
}

public class SettingsTests
{
    [Fact]
    public void Saves_and_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Load();
            store.Update(s => { s.Theme = ThemeMode.Dark; s.Accent = "Iris"; s.SearchEngineId = "duckduckgo"; s.DefaultZoom = 99; });
            var again = new SettingsStore(path).Load();
            Assert.Equal(ThemeMode.Dark, again.Theme);
            Assert.Equal("Iris", again.Accent);
            Assert.Equal("duckduckgo", again.SearchEngineId);
            Assert.Equal(5, again.DefaultZoom); // clamped
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var s = new SettingsStore(path).Load();
            Assert.Equal(StartupBehavior.NewTabPage, s.Startup);
        }
        finally { File.Delete(path); File.Delete(path + ".bad"); }
    }

    [Fact]
    public void Date_groups()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0);
        Assert.Equal("Today", DateGrouping.Label(now.AddHours(-2), now));
        Assert.Equal("Yesterday", DateGrouping.Label(now.AddDays(-1), now));
    }
}

public class FuzzyMatcherTests
{
    [Fact]
    public void Matches_in_order_and_ranks_sensibly()
    {
        Assert.True(Relay.Core.Navigation.FuzzyMatcher.Score("npw", "New private window") > 0);
        Assert.Equal(-1, Relay.Core.Navigation.FuzzyMatcher.Score("xyz", "New tab"));
        var prefix = Relay.Core.Navigation.FuzzyMatcher.Score("hist", "History");
        var middle = Relay.Core.Navigation.FuzzyMatcher.Score("hist", "Clear history");
        var scattered = Relay.Core.Navigation.FuzzyMatcher.Score("hsy", "History");
        Assert.True(prefix > middle);
        Assert.True(middle > scattered);
        Assert.True(scattered > 0);
    }

    [Fact]
    public void Yahoo_is_available_and_has_a_description()
    {
        Assert.Contains(SearchEngines.BuiltIn, e => e.Id == "yahoo");
        Assert.All(SearchEngines.BuiltIn, e => Assert.False(string.IsNullOrEmpty(SearchEngines.Describe(e.Id))));
        Assert.Equal("https://search.yahoo.com/search?p=cats", SearchEngines.Resolve("yahoo", null).BuildSearchUri("cats").ToString());
    }
}
