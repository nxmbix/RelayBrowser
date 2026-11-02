using Relay.Core.Models;

namespace Relay.Data;

public sealed class HistoryRepository(RelayDatabase db)
{
    public Task RecordVisitAsync(string url, string title) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 8192) return;
        using var c = db.Open();
        c.Execute("""
            INSERT INTO history (url, title, visited_at, visit_count) VALUES ($u, $t, $v, 1)
            ON CONFLICT(url) DO UPDATE SET
                visited_at = excluded.visited_at,
                visit_count = visit_count + 1,
                title = CASE WHEN excluded.title <> '' THEN excluded.title ELSE title END;
            """, ("$u", url), ("$t", title ?? string.Empty), ("$v", RelayDatabase.ToUnix(DateTime.UtcNow)));
    });

    public Task UpdateTitleAsync(string url, string title) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        using var c = db.Open();
        c.Execute("UPDATE history SET title = $t WHERE url = $u", ("$t", title), ("$u", url));
    });

    public Task<List<HistoryEntry>> GetRecentAsync(int limit = 200, int offset = 0) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("SELECT id, url, title, visited_at, visit_count FROM history ORDER BY visited_at DESC LIMIT $l OFFSET $o",
            Map, ("$l", limit), ("$o", offset));
    });

    public Task<List<HistoryEntry>> SearchAsync(string term, int limit = 200) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("""
            SELECT id, url, title, visited_at, visit_count FROM history
            WHERE title LIKE $p ESCAPE '\' OR url LIKE $p ESCAPE '\'
            ORDER BY visited_at DESC LIMIT $l
            """, Map, ("$p", SqlExtensions.LikePattern(term)), ("$l", limit));
    });

    /// <summary>Suggestions for the address bar: frequently and recently visited pages matching the text.</summary>
    public Task<List<HistoryEntry>> SuggestAsync(string term, int limit = 5) => Task.Run(() =>
    {
        using var c = db.Open();
        var stripped = term.Replace("https://", "").Replace("http://", "").Replace("www.", "");
        return c.Query("""
            SELECT id, url, title, visited_at, visit_count FROM history
            WHERE url LIKE $p ESCAPE '\' OR title LIKE $p ESCAPE '\'
            ORDER BY (CASE WHEN url LIKE $prefix ESCAPE '\' OR url LIKE $wprefix ESCAPE '\' THEN 0 ELSE 1 END),
                     visit_count DESC, visited_at DESC
            LIMIT $l
            """, Map,
            ("$p", SqlExtensions.LikePattern(stripped)),
            ("$prefix", "https://" + SqlExtensions.LikePattern(stripped)[1..]),
            ("$wprefix", "https://www." + SqlExtensions.LikePattern(stripped)[1..]),
            ("$l", limit));
    });

    /// <summary>Recently visited web pages (newest first) for the new tab page.</summary>
    public Task<List<HistoryEntry>> RecentPagesAsync(int limit = 40) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("""
            SELECT id, url, title, visited_at, visit_count FROM history
            WHERE url LIKE 'http%'
            ORDER BY visited_at DESC LIMIT $l
            """, Map, ("$l", limit));
    });

    public Task DeleteAsync(long id) => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("DELETE FROM history WHERE id = $id", ("$id", id));
    });

    public Task ClearAsync(DateTime? sinceUtc = null) => Task.Run(() =>
    {
        using var c = db.Open();
        if (sinceUtc is null) c.Execute("DELETE FROM history");
        else c.Execute("DELETE FROM history WHERE visited_at >= $s", ("$s", RelayDatabase.ToUnix(sinceUtc.Value)));
    });

    private static HistoryEntry Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Url = r.GetString(1),
        Title = r.GetString(2),
        VisitedAt = RelayDatabase.FromUnix(r.GetInt64(3)),
        VisitCount = r.GetInt32(4),
    };
}
