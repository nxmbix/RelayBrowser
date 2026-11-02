using Microsoft.Data.Sqlite;

namespace Relay.Data;

/// <summary>
/// Owns the SQLite database file: connection creation and schema migrations.
/// Connections are short-lived and pooled by Microsoft.Data.Sqlite.
/// </summary>
public sealed class RelayDatabase
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;

    public RelayDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Creates an in-memory database (tests). Keeps one connection alive so data persists.</summary>
    public static RelayDatabase InMemory(string name)
    {
        var db = new RelayDatabase(name, inMemory: true);
        return db;
    }

    private SqliteConnection? _keepAlive;

    private RelayDatabase(string name, bool inMemory)
    {
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"));
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        if (version < 1) CreateV1(c, tx);
        if (version < 2)
        {
            // v2: tab groups are part of the saved session.
            Exec(c, tx, """
                ALTER TABLE session_tabs ADD COLUMN group_name TEXT NULL;
                ALTER TABLE session_tabs ADD COLUMN group_color TEXT NULL;
                ALTER TABLE session_tabs ADD COLUMN group_collapsed INTEGER NOT NULL DEFAULT 0;
                """);
        }
        // v3: pinned tabs.
        if (version < 3) Exec(c, tx, "ALTER TABLE session_tabs ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;");
        Exec(c, tx, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    private static void CreateV1(SqliteConnection c, SqliteTransaction tx)
    {
        Exec(c, tx, """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL DEFAULT '',
                visited_at INTEGER NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_history_visited ON history(visited_at DESC);

            CREATE TABLE IF NOT EXISTS bookmarks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                parent_id INTEGER NULL REFERENCES bookmarks(id) ON DELETE CASCADE,
                is_folder INTEGER NOT NULL DEFAULT 0,
                title TEXT NOT NULL DEFAULT '',
                url TEXT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_bookmarks_parent ON bookmarks(parent_id, position);
            CREATE INDEX IF NOT EXISTS ix_bookmarks_url ON bookmarks(url);

            CREATE TABLE IF NOT EXISTS quick_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                position INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS reading_list (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                url TEXT NOT NULL UNIQUE,
                added_at INTEGER NOT NULL,
                is_read INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                file_path TEXT NOT NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL,
                failure_reason TEXT NULL,
                started_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS permissions (
                origin TEXT NOT NULL,
                kind TEXT NOT NULL,
                decision INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY (origin, kind)
            );

            CREATE TABLE IF NOT EXISTS favicons (
                host TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_tabs (
                window_index INTEGER NOT NULL,
                position INTEGER NOT NULL,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                is_active INTEGER NOT NULL DEFAULT 0
            );
            """);

        // Seed a few starter quick links on first run.
        Exec(c, tx, """
            INSERT INTO quick_links (title, url, position) VALUES
              ('Wikipedia', 'https://www.wikipedia.org/', 0),
              ('YouTube', 'https://www.youtube.com/', 1),
              ('GitHub', 'https://github.com/', 2),
              ('Weather', 'https://www.yr.no/', 3);
            """);
    }

    internal static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public static DateTime FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
