using System.Text;
using Microsoft.Data.Sqlite;
using Relay.Core.Passwords;

namespace Relay.Data;

/// <summary>Encrypts secrets at rest (Windows DPAPI in the app; a fake in tests).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] cipher);
}

public sealed record SavedLogin(long Id, string Origin, string Username);

/// <summary>RELAY's own password store: one encrypted entry per (origin, username). Passwords are only decrypted on request.</summary>
public sealed class PasswordRepository(RelayDatabase db, ISecretProtector protector)
{
    public event EventHandler? Changed;

    public async Task<int> ImportAsync(IReadOnlyList<ImportedLogin> logins)
    {
        var n = await Task.Run(() =>
        {
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO passwords (origin, username, secret, created_at) VALUES ($o, $u, $s, $t)
                ON CONFLICT(origin, username) DO UPDATE SET secret = excluded.secret;
                """;
            var o = cmd.Parameters.Add("$o", SqliteType.Text);
            var u = cmd.Parameters.Add("$u", SqliteType.Text);
            var s = cmd.Parameters.Add("$s", SqliteType.Blob);
            cmd.Parameters.AddWithValue("$t", RelayDatabase.ToUnix(DateTime.UtcNow));
            var count = 0;
            foreach (var l in logins)
            {
                o.Value = l.Origin;
                u.Value = l.Username;
                s.Value = protector.Protect(Encoding.UTF8.GetBytes(l.Password));
                count += cmd.ExecuteNonQuery() > 0 ? 1 : 0;
            }
            tx.Commit();
            return count;
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return n;
    }

    public Task<List<SavedLogin>> ForOriginAsync(string origin) => Task.Run(() => Query("WHERE origin = $o", ("$o", origin)));

    public Task<List<SavedLogin>> AllAsync() => Task.Run(() => Query(string.Empty));

    public Task<string?> GetPasswordAsync(long id) => Task.Run(() =>
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT secret FROM passwords WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] cipher) return null;
        try { return Encoding.UTF8.GetString(protector.Unprotect(cipher)); }
        catch { return null; } // encrypted for another Windows account
    });

    public async Task DeleteAsync(long id)
    {
        await Task.Run(() => Exec("DELETE FROM passwords WHERE id = $id", ("$id", id)));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync()
    {
        await Task.Run(() => Exec("DELETE FROM passwords"));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<int> CountAsync() => Task.Run(() =>
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM passwords";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    private List<SavedLogin> Query(string where, params (string, object)[] args)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, origin, username FROM passwords {where} ORDER BY origin, username";
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        using var r = cmd.ExecuteReader();
        var list = new List<SavedLogin>();
        while (r.Read()) list.Add(new SavedLogin(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    private void Exec(string sql, params (string, object)[] args)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}
