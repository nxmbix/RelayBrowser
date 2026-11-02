using Relay.Core.Bookmarks;
using Relay.Core.Models;
using Relay.Data;
using Xunit;

namespace Relay.Tests;

public sealed class DataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}.db");
    private readonly RelayDatabase _db;

    public DataTests()
    {
        _db = new RelayDatabase(_path);
        _db.Migrate();
        _db.Migrate(); // idempotent
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public async Task History_records_and_counts_visits()
    {
        var h = new HistoryRepository(_db);
        await h.RecordVisitAsync("https://example.com/", "Example");
        await h.RecordVisitAsync("https://example.com/", "");
        await h.RecordVisitAsync("https://other.org/", "Other");
        var all = await h.GetRecentAsync();
        Assert.Equal(2, all.Count);
        var ex = all.Single(e => e.Url == "https://example.com/");
        Assert.Equal(2, ex.VisitCount);
        Assert.Equal("Example", ex.Title); // empty title does not overwrite

        Assert.Single(await h.SearchAsync("other"));
        Assert.Equal("https://example.com/", (await h.SuggestAsync("exa"))[0].Url);

        await h.DeleteAsync(ex.Id);
        Assert.Single(await h.GetRecentAsync());
        await h.ClearAsync();
        Assert.Empty(await h.GetRecentAsync());
    }

    [Fact]
    public async Task History_search_escapes_wildcards()
    {
        var h = new HistoryRepository(_db);
        await h.RecordVisitAsync("https://a.com/", "100% real");
        await h.RecordVisitAsync("https://b.com/", "1000 things");
        Assert.Single(await h.SearchAsync("100%"));
    }

    [Fact]
    public async Task Bookmarks_folders_move_and_cascade()
    {
        var b = new BookmarkRepository(_db);
        var folder = await b.AddAsync("Work", null, null, isFolder: true);
        var sub = await b.AddAsync("Sub", null, folder.Id, isFolder: true);
        var bm = await b.AddAsync("Example", "https://example.com/", null);
        await b.UpdateAsync(bm.Id, "Example!", "https://example.com/", sub.Id);

        var found = await b.FindByUrlAsync("https://example.com/");
        Assert.NotNull(found);
        Assert.Equal(sub.Id, found!.ParentId);
        Assert.Equal("Example!", found.Title);

        // A folder cannot be moved inside its own child.
        await b.UpdateAsync(folder.Id, "Work", null, sub.Id);
        Assert.Null((await b.GetAllAsync()).Single(x => x.Id == folder.Id).ParentId);

        await b.DeleteAsync(folder.Id);
        Assert.Empty(await b.GetAllAsync());
    }

    [Fact]
    public async Task Bookmarks_reorder()
    {
        var b = new BookmarkRepository(_db);
        var a = await b.AddAsync("A", "https://a.com/", null);
        var c = await b.AddAsync("B", "https://b.com/", null);
        var d = await b.AddAsync("C", "https://c.com/", null);
        await b.ReorderAsync(d.Id, 0);
        var order = (await b.GetAllAsync()).OrderBy(x => x.Position).Select(x => x.Title).ToList();
        Assert.Equal(["C", "A", "B"], order);
    }

    [Fact]
    public async Task Bookmarks_import_export()
    {
        var b = new BookmarkRepository(_db);
        var root = new BookmarkNode();
        var f = new BookmarkNode { Title = "Folder" };
        f.Children.Add(new BookmarkNode { Title = "One", Url = "https://one.example/" });
        root.Children.Add(f);
        root.Children.Add(new BookmarkNode { Title = "Two", Url = "https://two.example/" });
        Assert.Equal(2, await b.ImportAsync(root, "Imported"));

        var tree = await b.ExportTreeAsync();
        Assert.Equal("Imported", tree.Children[0].Title);
        Assert.Equal(2, tree.CountBookmarks());
    }

    [Fact]
    public async Task Quick_links_seeded_and_reorderable()
    {
        var q = new QuickLinkRepository(_db);
        var links = await q.GetAllAsync();
        Assert.Equal(4, links.Count);
        await q.AddAsync("Test", "https://test.example/");
        var ids = (await q.GetAllAsync()).Select(l => l.Id).Reverse().ToList();
        await q.ReorderAsync(ids);
        Assert.Equal("Test", (await q.GetAllAsync())[0].Title);
    }

    [Fact]
    public async Task Permissions_and_reading_list_and_downloads()
    {
        var p = new PermissionRepository(_db);
        await p.SetAsync("https://meet.example", "Camera", PermissionDecision.Allow);
        Assert.Equal(PermissionDecision.Allow, await p.GetAsync("https://meet.example", "Camera"));
        await p.SetAsync("https://meet.example", "Camera", PermissionDecision.Ask);
        Assert.Equal(PermissionDecision.Ask, await p.GetAsync("https://meet.example", "Camera"));

        var r = new ReadingListRepository(_db);
        await r.AddAsync("Article", "https://a.example/post");
        await r.AddAsync("Article", "https://a.example/post");
        var items = await r.GetAllAsync();
        Assert.Single(items);
        await r.SetReadAsync(items[0].Id, true);
        Assert.True((await r.GetAllAsync())[0].IsRead);

        var d = new DownloadRepository(_db);
        var id = await d.InsertAsync(new DownloadRecord { Url = "https://x/y.zip", FilePath = "y.zip", Status = DownloadStatus.InProgress });
        await d.MarkInterruptedAsync();
        var rec = (await d.GetRecentAsync()).Single(x => x.Id == id);
        Assert.Equal(DownloadStatus.Failed, rec.Status);
    }

    [Fact]
    public async Task Session_round_trip()
    {
        var s = new SessionRepository(_db);
        await s.SaveAsync([
            new SessionTab { WindowIndex = 0, Position = 0, Url = "https://a/", Title = "A" },
            new SessionTab { WindowIndex = 0, Position = 1, Url = "https://b/", Title = "B", IsActive = true },
        ]);
        var tabs = await s.LoadAsync();
        Assert.Equal(2, tabs.Count);
        Assert.True(tabs[1].IsActive);
    }
}

public sealed class MigrationTests
{
    [Fact]
    public async Task Upgrades_v1_database_and_keeps_groups_in_session()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-mig-{Guid.NewGuid():N}.db");
        try
        {
            // Simulate a v1 database (as shipped in RELAY 1.0).
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE session_tabs (window_index INTEGER NOT NULL, position INTEGER NOT NULL, url TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', is_active INTEGER NOT NULL DEFAULT 0); PRAGMA user_version = 1;";
                cmd.ExecuteNonQuery();
            }
            var db = new RelayDatabase(path);
            db.Migrate();
            var s = new SessionRepository(db);
            await s.SaveAsync([new SessionTab { Url = "https://a/", GroupName = "Work", GroupColor = "Blue", GroupCollapsed = true }]);
            var t = (await s.LoadAsync()).Single();
            Assert.Equal("Work", t.GroupName);
            Assert.True(t.GroupCollapsed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { }
        }
    }
}
