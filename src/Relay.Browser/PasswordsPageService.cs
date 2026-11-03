using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Relay.Core.Navigation;

namespace Relay.Browser;

/// <summary>
/// Data bridge for relay://passwords (https://passwords.relay/). The page gets the list of logins
/// without their passwords; a password is decrypted only when the user asks to see or copy it.
/// </summary>
public sealed class PasswordsPageService
{
    private readonly BrowserServices _s;

    public PasswordsPageService(BrowserServices services)
    {
        _s = services;
        _s.Passwords.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _s.Settings.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tabs showing the passwords page should refresh their data.</summary>
    public event EventHandler? StateChanged;

    public static bool IsTrustedSource(string? source) => UrlDisplay.IsPasswords(source);

    public async Task<string> BuildStateJsonAsync(bool isPrivate)
    {
        var logins = new JsonArray();
        foreach (var l in await _s.Passwords.AllAsync())
            logins.Add(new JsonObject { ["id"] = l.Id, ["origin"] = l.Origin, ["host"] = UrlDisplay.Host(l.Origin), ["username"] = l.Username });

        return new JsonObject
        {
            ["type"] = "state",
            ["private"] = isPrivate,
            ["theme"] = _s.IsDarkTheme ? "dark" : "light",
            ["accent"] = _s.AccentHex,
            ["offerToSave"] = _s.Settings.Current.OfferToSavePasswords,
            ["logins"] = logins,
        }.ToJsonString();
    }

    /// <summary>Handles a message from the page. Unknown or malformed messages are ignored.</summary>
    public async Task HandleMessageAsync(BrowserTab tab, string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (msg is not JsonObject o) return;

        var type = o["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : string.Empty;
        long id = o["id"] is JsonValue iv && iv.TryGetValue<long>(out var l) ? l : 0;

        switch (type)
        {
            case "ready":
                await tab.PushPasswordsStateAsync();
                break;

            case "reveal":
            {
                if (id <= 0) return;
                var pw = await _s.Passwords.GetPasswordAsync(id);
                tab.PostPasswordsMessage(new JsonObject { ["type"] = "secret", ["id"] = id, ["password"] = pw }.ToJsonString());
                break;
            }

            case "copy-password":
            {
                if (id <= 0) return;
                var pw = await _s.Passwords.GetPasswordAsync(id);
                if (pw is null) { Toast(tab, "Couldn't read that password. It was saved by another Windows account or PC."); return; }
                Toast(tab, CopyToClipboard(pw) ? "Password copied" : "Couldn't use the clipboard. Try again.");
                break;
            }

            case "copy-username":
            {
                var login = (await _s.Passwords.AllAsync()).FirstOrDefault(x => x.Id == id);
                if (login is null) return;
                Toast(tab, CopyToClipboard(login.Username) ? "Username copied" : "Couldn't use the clipboard. Try again.");
                break;
            }

            case "delete":
                if (id > 0) await _s.Passwords.DeleteAsync(id);
                break;

            case "clear":
                await _s.Passwords.ClearAsync();
                break;

            case "import":
                await _s.Ui.ImportPasswordsAsync();
                break;

            case "open":
            {
                // Only the http(s) origin of a saved login.
                var login = (await _s.Passwords.AllAsync()).FirstOrDefault(x => x.Id == id);
                if (login is not null && Uri.TryCreate(login.Origin, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                    tab.Navigate(u.AbsoluteUri);
                break;
            }

            case "offer-to-save":
            {
                var on = o["value"] is JsonValue b && b.TryGetValue<bool>(out var v) && v;
                _s.Settings.Update(s => s.OfferToSavePasswords = on);
                break;
            }
        }
    }

    private static void Toast(BrowserTab tab, string message) =>
        tab.PostPasswordsMessage(new JsonObject { ["type"] = "toast", ["message"] = message }.ToJsonString());

    private static bool CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch { return false; } // clipboard busy
    }
}
