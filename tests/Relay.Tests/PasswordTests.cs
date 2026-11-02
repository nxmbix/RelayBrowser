using System.Text;
using Microsoft.Data.Sqlite;
using Relay.Core.Passwords;
using Relay.Data;
using Xunit;

namespace Relay.Tests;

public sealed class PasswordTests : IDisposable
{
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)1).ToArray();
        public byte[] Unprotect(byte[] c) => c[0] == 1 ? c.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray() : throw new InvalidDataException();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"relay-pw-{Guid.NewGuid():N}");
    public PasswordTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Parses_chrome_and_firefox_exports()
    {
        var chrome = "\uFEFFname,url,username,password,note\r\nex,https://Example.com/login?x=1,nik,\"p,a\"\"ss\",\r\n" +
                     "bad,javascript:alert(1),u,p,\r\napp,android://hash@com.app/,u,p,\r\nmulti,https://a.example:8443/,,\"line1\nline2\",\r\n";
        var logins = PasswordCsv.Parse(chrome);
        Assert.Equal(2, logins.Count);
        Assert.Equal(new ImportedLogin("https://example.com", "nik", "p,a\"ss"), logins[0]);
        Assert.Equal(new ImportedLogin("https://a.example:8443", "", "line1\nline2"), logins[1]);

        var firefox = "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\"\n\"https://mozilla.org\",\"me\",\"secret\",,\"https://mozilla.org\",\"{1}\"\n";
        Assert.Equal(new ImportedLogin("https://mozilla.org", "me", "secret"), Assert.Single(PasswordCsv.Parse(firefox)));
        Assert.Empty(PasswordCsv.Parse("a,b\n1,2\n"));
    }

    [Fact]
    public void Origins_are_exact_and_web_only()
    {
        Assert.Equal("https://example.com", PasswordOrigin.From("https://EXAMPLE.com:443/a"));
        Assert.Equal("http://example.com:8080", PasswordOrigin.From("http://example.com:8080/"));
        Assert.NotEqual(PasswordOrigin.From("https://evil-example.com"), PasswordOrigin.From("https://example.com"));
        Assert.Null(PasswordOrigin.From("file:///C:/x.html"));
        Assert.Null(PasswordOrigin.From("https://newtab.relay") is { } o && o.StartsWith("javascript") ? "x" : null);
    }

    [Fact]
    public async Task Stores_encrypted_and_scopes_by_origin()
    {
        var db = new RelayDatabase(Path.Combine(_dir, "relay.db"));
        db.Migrate();
        var repo = new PasswordRepository(db, new XorProtector());
        Assert.Equal(2, await repo.ImportAsync([new("https://example.com", "nik", "hunter2"), new("https://other.com", "", "x")]));
        await repo.ImportAsync([new("https://example.com", "nik", "changed")]); // same login: updated, not duplicated
        var mine = Assert.Single(await repo.ForOriginAsync("https://example.com"));
        Assert.Equal("changed", await repo.GetPasswordAsync(mine.Id));
        Assert.Empty(await repo.ForOriginAsync("https://example.com.evil.net"));

        using (var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "relay.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT secret FROM passwords WHERE id = " + mine.Id;
            var raw = (byte[])cmd.ExecuteScalar()!;
            Assert.DoesNotContain("changed", Encoding.UTF8.GetString(raw));
        }
        await repo.ClearAsync();
        Assert.Equal(0, await repo.CountAsync());
    }
}
