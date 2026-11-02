using Microsoft.Data.Sqlite;

namespace Relay.Data;

internal static class SqlExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static int Execute(this SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        return cmd.ExecuteScalar();
    }

    /// <summary>Escapes LIKE wildcards; use with ESCAPE '\'.</summary>
    public static string LikePattern(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static string? GetNullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
