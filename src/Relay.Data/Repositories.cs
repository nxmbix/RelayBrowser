using Relay.Core.Models;

namespace Relay.Data;

public sealed class QuickLinkRepository(RelayDatabase db)
{
    public event EventHandler? Changed;

    public Task<List<QuickLink>> GetAllAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("SELECT id, title, url, position FROM quick_links ORDER BY position, id",
            r => new QuickLink { Id = r.GetInt64(0), Title = r.GetString(1), Url = r.GetString(2), Position = r.GetInt32(3) });
    });

    public async Task AddAsync(string title, string url)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("INSERT INTO quick_links (title, url, position) VALUES ($t, $u, (SELECT COALESCE(MAX(position), -1) + 1 FROM quick_links))",
                ("$t", title), ("$u", url));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(long id, string title, string url)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("UPDATE quick_links SET title = $t, url = $u WHERE id = $id", ("$t", title), ("$u", url), ("$id", id));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(long id)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("DELETE FROM quick_links WHERE id = $id", ("$id", id));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persists a new order given the full list of ids.</summary>
    public async Task ReorderAsync(IReadOnlyList<long> orderedIds)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            for (var i = 0; i < orderedIds.Count; i++)
            {
                using var cmd = c.Cmd("UPDATE quick_links SET position = $p WHERE id = $id", ("$p", i), ("$id", orderedIds[i]));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ReadingListRepository(RelayDatabase db)
{
    public event EventHandler? Changed;

    public Task<List<ReadingListItem>> GetAllAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("SELECT id, title, url, added_at, is_read FROM reading_list ORDER BY is_read, added_at DESC",
            r => new ReadingListItem
            {
                Id = r.GetInt64(0), Title = r.GetString(1), Url = r.GetString(2),
                AddedAt = RelayDatabase.FromUnix(r.GetInt64(3)), IsRead = r.GetInt64(4) != 0,
            });
    });

    public async Task AddAsync(string title, string url)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("""
                INSERT INTO reading_list (title, url, added_at, is_read) VALUES ($t, $u, $a, 0)
                ON CONFLICT(url) DO UPDATE SET is_read = 0, added_at = excluded.added_at, title = excluded.title
                """, ("$t", title), ("$u", url), ("$a", RelayDatabase.ToUnix(DateTime.UtcNow)));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetReadAsync(long id, bool isRead)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("UPDATE reading_list SET is_read = $r WHERE id = $id", ("$r", isRead ? 1 : 0), ("$id", id));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(long id)
    {
        await Task.Run(() =>
        {
            using var c = db.Open();
            c.Execute("DELETE FROM reading_list WHERE id = $id", ("$id", id));
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class DownloadRepository(RelayDatabase db)
{
    public Task<List<DownloadRecord>> GetRecentAsync(int limit = 100) => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("SELECT id, url, file_path, total_bytes, received_bytes, status, failure_reason, started_at FROM downloads ORDER BY started_at DESC LIMIT $l",
            r => new DownloadRecord
            {
                Id = r.GetInt64(0), Url = r.GetString(1), FilePath = r.GetString(2), TotalBytes = r.GetInt64(3),
                ReceivedBytes = r.GetInt64(4), Status = (DownloadStatus)r.GetInt32(5), FailureReason = r.GetNullableString(6),
                StartedAt = RelayDatabase.FromUnix(r.GetInt64(7)),
            }, ("$l", limit));
    });

    public Task<long> InsertAsync(DownloadRecord d) => Task.Run(() =>
    {
        using var c = db.Open();
        return Convert.ToInt64(c.Scalar("""
            INSERT INTO downloads (url, file_path, total_bytes, received_bytes, status, failure_reason, started_at)
            VALUES ($u, $f, $t, $r, $s, $fr, $st); SELECT last_insert_rowid();
            """, ("$u", d.Url), ("$f", d.FilePath), ("$t", d.TotalBytes), ("$r", d.ReceivedBytes), ("$s", (int)d.Status),
            ("$fr", d.FailureReason), ("$st", RelayDatabase.ToUnix(d.StartedAt))));
    });

    public Task UpdateAsync(DownloadRecord d) => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("UPDATE downloads SET file_path = $f, total_bytes = $t, received_bytes = $r, status = $s, failure_reason = $fr WHERE id = $id",
            ("$f", d.FilePath), ("$t", d.TotalBytes), ("$r", d.ReceivedBytes), ("$s", (int)d.Status), ("$fr", d.FailureReason), ("$id", d.Id));
    });

    public Task DeleteAsync(long id) => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("DELETE FROM downloads WHERE id = $id", ("$id", id));
    });

    public Task ClearFinishedAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("DELETE FROM downloads WHERE status <> $s", ("$s", (int)DownloadStatus.InProgress));
    });

    /// <summary>Downloads still marked in progress from a previous run were interrupted.</summary>
    public Task MarkInterruptedAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("UPDATE downloads SET status = $f, failure_reason = 'Interrupted when RELAY closed' WHERE status IN ($a, $b)",
            ("$f", (int)DownloadStatus.Failed), ("$a", (int)DownloadStatus.InProgress), ("$b", (int)DownloadStatus.Paused));
    });
}

public sealed class PermissionRepository(RelayDatabase db)
{
    public Task<List<SitePermission>> GetAllAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("SELECT origin, kind, decision, updated_at FROM permissions ORDER BY origin, kind",
            r => new SitePermission { Origin = r.GetString(0), Kind = r.GetString(1), Decision = (PermissionDecision)r.GetInt32(2), UpdatedAt = RelayDatabase.FromUnix(r.GetInt64(3)) });
    });

    public Task<PermissionDecision> GetAsync(string origin, string kind) => Task.Run(() =>
    {
        using var c = db.Open();
        var v = c.Scalar("SELECT decision FROM permissions WHERE origin = $o AND kind = $k", ("$o", origin), ("$k", kind));
        return v is null ? PermissionDecision.Ask : (PermissionDecision)Convert.ToInt32(v);
    });

    public Task SetAsync(string origin, string kind, PermissionDecision decision) => Task.Run(() =>
    {
        using var c = db.Open();
        if (decision == PermissionDecision.Ask)
            c.Execute("DELETE FROM permissions WHERE origin = $o AND kind = $k", ("$o", origin), ("$k", kind));
        else
            c.Execute("""
                INSERT INTO permissions (origin, kind, decision, updated_at) VALUES ($o, $k, $d, $u)
                ON CONFLICT(origin, kind) DO UPDATE SET decision = excluded.decision, updated_at = excluded.updated_at
                """, ("$o", origin), ("$k", kind), ("$d", (int)decision), ("$u", RelayDatabase.ToUnix(DateTime.UtcNow)));
    });

    public Task ClearAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("DELETE FROM permissions");
    });
}

public sealed class FaviconRepository(RelayDatabase db)
{
    public Task<byte[]?> GetAsync(string host) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(host)) return null;
        using var c = db.Open();
        return c.Scalar("SELECT data FROM favicons WHERE host = $h", ("$h", host)) as byte[];
    });

    public Task SetAsync(string host, byte[] png) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(host) || png.Length == 0 || png.Length > 512 * 1024) return;
        using var c = db.Open();
        c.Execute("""
            INSERT INTO favicons (host, data, updated_at) VALUES ($h, $d, $u)
            ON CONFLICT(host) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at
            """, ("$h", host), ("$d", png), ("$u", RelayDatabase.ToUnix(DateTime.UtcNow)));
    });

    public Task ClearAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        c.Execute("DELETE FROM favicons");
    });
}

public sealed class SessionRepository(RelayDatabase db)
{
    public Task SaveAsync(IReadOnlyList<SessionTab> tabs) => Task.Run(() => Save(tabs));

    /// <summary>Synchronous save used during shutdown.</summary>
    public void Save(IReadOnlyList<SessionTab> tabs)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var del = c.Cmd("DELETE FROM session_tabs")) { del.Transaction = tx; del.ExecuteNonQuery(); }
        foreach (var t in tabs)
        {
            using var cmd = c.Cmd("""
                INSERT INTO session_tabs (window_index, position, url, title, is_active, group_name, group_color, group_collapsed, is_pinned)
                VALUES ($w, $p, $u, $t, $a, $gn, $gc, $gx, $pin)
                """,
                ("$w", t.WindowIndex), ("$p", t.Position), ("$u", t.Url), ("$t", t.Title), ("$a", t.IsActive ? 1 : 0),
                ("$gn", t.GroupName), ("$gc", t.GroupColor), ("$gx", t.GroupCollapsed ? 1 : 0), ("$pin", t.IsPinned ? 1 : 0));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Task<List<SessionTab>> LoadAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        return c.Query("""
            SELECT window_index, position, url, title, is_active, group_name, group_color, group_collapsed, is_pinned
            FROM session_tabs ORDER BY window_index, position
            """,
            r => new SessionTab
            {
                WindowIndex = r.GetInt32(0), Position = r.GetInt32(1), Url = r.GetString(2), Title = r.GetString(3), IsActive = r.GetInt64(4) != 0,
                GroupName = r.GetNullableString(5), GroupColor = r.GetNullableString(6), GroupCollapsed = r.GetInt64(7) != 0, IsPinned = r.GetInt64(8) != 0,
            });
    });
}
