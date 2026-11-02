using System.Text;

namespace Relay.Core.Passwords;

public sealed record ImportedLogin(string Origin, string Username, string Password);

public static class PasswordOrigin
{
    /// <summary>scheme://host[:port] for http/https pages; null for anything else (logins are bound to an exact origin).</summary>
    public static string? From(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrEmpty(u.Host)) return null;
        return u.IsDefaultPort ? $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}" : $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}";
    }
}

/// <summary>Reads the password CSV files that Chrome, Edge, Brave, Opera, Vivaldi and Firefox export.</summary>
public static class PasswordCsv
{
    public static List<ImportedLogin> Parse(string text)
    {
        var rows = ReadRows(text.TrimStart('\uFEFF'));
        var result = new List<ImportedLogin>();
        if (rows.Count < 2) return result;
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        var url = Col("url", "origin", "login_uri", "website", "web site");
        var user = Col("username", "login_username", "login", "user name", "email");
        var pass = Col("password", "login_password");
        if (url < 0 || pass < 0) return result;
        var seen = new HashSet<(string, string)>();
        foreach (var r in rows.Skip(1))
        {
            string Get(int i) => i >= 0 && i < r.Count ? r[i] : string.Empty;
            var origin = PasswordOrigin.From(Get(url));
            var password = Get(pass);
            if (origin is null || password.Length == 0 || password.Length > 4096) continue;
            var username = Get(user).Trim();
            if (username.Length > 1024 || !seen.Add((origin, username))) continue;
            result.Add(new ImportedLogin(origin, username, password));
        }
        return result;
    }

    private static List<List<string>> ReadRows(string s)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < s.Length && s[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
