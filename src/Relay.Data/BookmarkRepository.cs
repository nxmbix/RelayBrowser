using Microsoft.Data.Sqlite;
using Relay.Core.Bookmarks;
using Relay.Core.Models;

namespace Relay.Data;

public sealed class BookmarkRepository(RelayDatabase db)
{
    private const string Columns = "id, parent_id, is_folder, title, url, position, created_at";

    public event EventHandler? Changed;
    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<List<Bookmark>> GetAllAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query($"SELECT {Columns} FROM bookmarks ORDER BY parent_id, position, id", Map);
    });

    public Task<List<Bookmark>> SearchAsync(string term, int limit = 100) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query($"""
            SELECT {Columns} FROM bookmarks
            WHERE is_folder = 0 AND (title LIKE $p ESCAPE '\' OR url LIKE $p ESCAPE '\')
            ORDER BY title LIMIT $l
            """, Map, ("$p", SqlExtensions.LikePattern(term)), ("$l", limit));
    });

    public Task<Bookmark?> FindByUrlAsync(string url) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query($"SELECT {Columns} FROM bookmarks WHERE is_folder = 0 AND url = $u LIMIT 1", Map, ("$u", url)).FirstOrDefault();
    });

    public async Task<Bookmark> AddAsync(string title, string? url, long? parentId, bool isFolder = false)
    {
        var b = await Task.Run(() =>
        {
            using var c = db.Open();
            return Insert(c, null, title, url, parentId, isFolder);
        });
        Raise();
        return b;
    }

    private static Bookmark Insert(SqliteConnection c, SqliteTransaction? tx, string title, string? url, long? parentId, bool isFolder)
    {
        using var posCmd = c.Cmd("SELECT COALESCE(MAX(position), -1) + 1 FROM bookmarks WHERE parent_id IS $p", ("$p", parentId));
        posCmd.Transaction = tx;
        var pos = Convert.ToInt32(posCmd.ExecuteScalar());
        var now = DateTime.UtcNow;
        using var cmd = c.Cmd("""
            INSERT INTO bookmarks (parent_id, is_folder, title, url, position, created_at)
            VALUES ($p, $f, $t, $u, $pos, $c); SELECT last_insert_rowid();
            """, ("$p", parentId), ("$f", isFolder ? 1 : 0), ("$t", title), ("$u", isFolder ? null : url), ("$pos", pos), ("$c", RelayDatabase.ToUnix(now)));
        cmd.Transaction = tx;
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return new Bookmark { Id = id, ParentId = parentId, IsFolder = isFolder, Title = title, Url = isFolder ? null : url, Position = pos, CreatedAt = now };
    }

    public async Task UpdateAsync(long id, string title, string? url, long? parentId)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            // Refuse to move a folder into itself or one of its descendants.
            if (parentId is not null && IsDescendantOrSelf(c, parentId.Value, id)) parentId = null;
            var current = c.Query("SELECT parent_id FROM bookmarks WHERE id = $id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", id));
            var moved = current.Count == 1 && current[0] != parentId;
            if (moved)
            {
                var pos = Convert.ToInt32(c.Scalar("SELECT COALESCE(MAX(position), -1) + 1 FROM bookmarks WHERE parent_id IS $p", ("$p", parentId)));
                c.Execute("UPDATE bookmarks SET title = $t, url = CASE WHEN is_folder = 1 THEN NULL ELSE $u END, parent_id = $p, position = $pos WHERE id = $id",
                    ("$t", title), ("$u", url), ("$p", parentId), ("$pos", pos), ("$id", id));
            }
            else
            {
                c.Execute("UPDATE bookmarks SET title = $t, url = CASE WHEN is_folder = 1 THEN NULL ELSE $u END WHERE id = $id",
                    ("$t", title), ("$u", url), ("$id", id));
            }
        });
        Raise();
    }

    private static bool IsDescendantOrSelf(SqliteConnection c, long candidate, long ancestor)
    {
        long? current = candidate;
        var guard = 0;
        while (current is not null && guard++ < 1000)
        {
            if (current == ancestor) return true;
            var parent = c.Query("SELECT parent_id FROM bookmarks WHERE id = $id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", current));
            current = parent.Count == 0 ? null : parent[0];
        }
        return false;
    }

    /// <summary>Moves an item to <paramref name="newIndex"/> among its siblings.</summary>
    public async Task ReorderAsync(long id, int newIndex)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            var parent = c.Query("SELECT parent_id FROM bookmarks WHERE id = $id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", id));
            if (parent.Count == 0) return;
            using var tx = c.BeginTransaction();
            using var q = c.Cmd("SELECT id FROM bookmarks WHERE parent_id IS $p ORDER BY position, id", ("$p", parent[0]));
            q.Transaction = tx;
            var ids = new List<long>();
            using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
            ids.Remove(id);
            ids.Insert(Math.Clamp(newIndex, 0, ids.Count), id);
            for (var i = 0; i < ids.Count; i++)
            {
                using var u = c.Cmd("UPDATE bookmarks SET position = $i WHERE id = $id", ("$i", i), ("$id", ids[i]));
                u.Transaction = tx;
                u.ExecuteNonQuery();
            }
            tx.Commit();
        });
        Raise();
    }

    public async Task DeleteAsync(long id)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("DELETE FROM bookmarks WHERE id = $id", ("$id", id)); // children cascade
        });
        Raise();
    }

    /// <summary>Imports a parsed tree under a new folder. Returns the number of bookmarks added.</summary>
    public async Task<int> ImportAsync(BookmarkNode root, string folderTitle)
    {
        var count = await Task.Run(() =>
        {
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            var folder = Insert(c, tx, folderTitle, null, null, true);
            var n = ImportChildren(c, tx, root.Children, folder.Id, 0);
            tx.Commit();
            return n;
        });
        Raise();
        return count;
    }

    private static int ImportChildren(SqliteConnection c, SqliteTransaction tx, List<BookmarkNode> nodes, long parentId, int depth)
    {
        if (depth > 50) return 0;
        var n = 0;
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                var f = Insert(c, tx, node.Title, null, parentId, true);
                n += ImportChildren(c, tx, node.Children, f.Id, depth + 1);
            }
            else
            {
                Insert(c, tx, node.Title, node.Url, parentId, false);
                n++;
            }
        }
        return n;
    }

    /// <summary>Builds the full tree for export.</summary>
    public async Task<BookmarkNode> ExportTreeAsync()
    {
        var all = await GetAllAsync();
        var root = new BookmarkNode { Title = "Bookmarks" };
        var lookup = all.ToLookup(b => b.ParentId);
        void Fill(BookmarkNode node, long? parentId, int depth)
        {
            if (depth > 50) return;
            foreach (var b in lookup[parentId].OrderBy(b => b.Position))
            {
                var child = new BookmarkNode { Title = b.Title, Url = b.IsFolder ? null : b.Url };
                node.Children.Add(child);
                if (b.IsFolder) Fill(child, b.Id, depth + 1);
            }
        }
        Fill(root, null, 0);
        return root;
    }

    private static Bookmark Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
        IsFolder = r.GetInt64(2) != 0,
        Title = r.GetString(3),
        Url = r.GetNullableString(4),
        Position = r.GetInt32(5),
        CreatedAt = RelayDatabase.FromUnix(r.GetInt64(6)),
    };
}
