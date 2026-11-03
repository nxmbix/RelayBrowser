using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Relay.App.Themes;
using Relay.App.ViewModels;
using Relay.App.Views;
using Relay.Browser;
using Relay.Core.Models;
using Relay.Core.Settings;
using ThemeMode = Relay.Core.Settings.ThemeMode;

namespace Relay.App.Services;

/// <summary>
/// Data bridge for relay://settings (https://settings.relay/). All changes go through
/// <see cref="SettingsViewModel"/>, the same logic the old Settings window used.
/// </summary>
public sealed class SettingsPageService : IInternalPage
{
    private readonly BrowserServices _s;
    private readonly SettingsViewModel _vm;

    public SettingsPageService(BrowserServices services, ThemeManager theme)
    {
        _s = services;
        _vm = new SettingsViewModel(services);
        _s.Settings.Changed += (_, _) => Raise();
        _s.Passwords.Changed += (_, _) => Raise();
        theme.ThemeChanged += (_, _) => Raise();
    }

    public event EventHandler? StateChanged;

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static Window? Owner => App.Current.WindowManager?.LastActive;

    // ---- State ---------------------------------------------------------------------------

    public async Task<string> BuildStateJsonAsync(BrowserTab tab)
    {
        var s = _s.Settings.Current;
        var passwordCount = 0;
        try { passwordCount = await _s.Passwords.CountAsync(); } catch { }

        static JsonArray Options<T>(IEnumerable<T> items, Func<T, JsonNode?> value, Func<T, string> text) =>
            new(items.Select(i => (JsonNode)new JsonObject { ["value"] = value(i), ["text"] = text(i) }).ToArray());

        return new JsonObject
        {
            ["type"] = "state",
            ["private"] = tab.IsPrivate,
            ["theme"] = _s.IsDarkTheme ? "dark" : "light",
            ["accentColor"] = _s.AccentHex,

            // General
            ["isDefaultBrowser"] = _vm.IsDefaultBrowser,
            ["defaultBrowserStatus"] = _vm.DefaultBrowserStatus,
            ["startup"] = s.Startup.ToString(),
            ["homepage"] = s.Homepage,
            ["homepageError"] = _vm.HomepageError,
            ["showHomeButton"] = s.ShowHomeButton,
            ["engine"] = _vm.Engine?.Id,
            ["engines"] = Options(_vm.Engines, e => e.Id, e => e.Name),
            ["isCustomEngine"] = _vm.IsCustomEngine,
            ["customEngine"] = s.CustomSearchTemplate ?? string.Empty,
            ["customEngineError"] = _vm.CustomEngineError,
            ["searchSuggestions"] = s.SearchSuggestions,
            ["sleepAfter"] = _vm.SleepAfter?.Minutes,
            ["sleepOptions"] = Options(_vm.SleepOptions, o => o.Minutes, o => o.Text),
            ["discardAfter"] = _vm.DiscardAfter?.Minutes,
            ["discardOptions"] = Options(_vm.DiscardOptions, o => o.Minutes, o => o.Text),
            ["tabHoverPreviews"] = s.TabHoverPreviews,

            // Appearance
            ["themeMode"] = s.Theme.ToString(),
            ["accent"] = s.Accent,
            ["accents"] = new JsonArray(ThemeManager.Accents.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Key,
                ["color"] = Hex(_s.IsDarkTheme ? a.Value.Dark : a.Value.Light),
            }).ToArray()),
            ["tabDensity"] = s.TabDensity.ToString(),
            ["showTabFavicons"] = s.ShowTabFavicons,
            ["sidebarEnabled"] = s.SidebarEnabled,
            ["pinnedTabsInSidebar"] = s.PinnedTabsInSidebar,
            ["verticalTabs"] = s.VerticalTabs,
            ["interfaceFont"] = s.InterfaceFont,
            ["fonts"] = Options(_vm.FontChoices, f => f, f => f),
            ["interfaceFontSize"] = s.InterfaceFontSize,
            ["fontSizes"] = Options(_vm.FontSizes, f => f, f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["defaultZoom"] = _vm.DefaultZoom?.Value,
            ["zooms"] = Options(_vm.Zooms, z => z.Value, z => z.Text),
            ["newTabShowClock"] = s.NewTabShowClock,
            ["newTabShowRecent"] = s.NewTabShowRecent,
            ["newTabShowBookmarks"] = s.NewTabShowBookmarks,
            ["backgroundSummary"] = _vm.BackgroundSummary,

            // Privacy
            ["passwordCount"] = passwordCount,
            ["offerToSavePasswords"] = s.OfferToSavePasswords,
            ["clearDataOnExit"] = s.ClearDataOnExit,
            ["passwordAutosave"] = s.PasswordAutosave,
            ["generalAutofill"] = s.GeneralAutofill,
            ["privateSearchSuggestions"] = s.PrivateSearchSuggestions,
            ["cookieStatus"] = _vm.CookieStatus,
            ["cookieSites"] = new JsonArray(_vm.CookieSites.Select(c => (JsonNode)new JsonObject { ["domain"] = c.Domain, ["count"] = c.Count }).ToArray()),
            ["permissions"] = new JsonArray(_vm.Permissions.Select(p => (JsonNode)new JsonObject
            {
                ["origin"] = p.Origin, ["kind"] = p.Kind, ["decision"] = p.Decision,
            }).ToArray()),

            // Blocking
            ["blockerEnabled"] = s.BlockerEnabled,
            ["blockerStatus"] = _vm.BlockerStatus,
            ["blockedTotal"] = _vm.BlockedTotalText,
            ["filterLists"] = new JsonArray(_vm.FilterLists.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.List.Id, ["name"] = l.Name, ["description"] = l.Description, ["updated"] = l.Updated, ["enabled"] = l.IsEnabled,
            }).ToArray()),
            ["allowedSites"] = new JsonArray(s.BlockerAllowedSites.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
            ["customRules"] = s.BlockerCustomRules,

            // Downloads
            ["downloadDirectory"] = _vm.DownloadDirectory,
            ["askWhereToSave"] = s.AskWhereToSave,
            ["downloadNotifications"] = s.DownloadNotifications,

            // About
            ["appVersion"] = _vm.AppVersion,
            ["engineVersion"] = _vm.EngineVersion,
            ["sdkVersion"] = _vm.SdkVersion,
            ["dotNetVersion"] = _vm.DotNetVersion,
            ["storageMode"] = _vm.StorageMode,
            ["dataFolder"] = _vm.DataFolder,
        }.ToJsonString();
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- Messages ------------------------------------------------------------------------

    public async Task HandleMessageAsync(BrowserTab tab, string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (msg is not JsonObject o) return;

        string Str(string key, int max = 4096)
        {
            var v = o[key] is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : string.Empty;
            return v.Length > max ? v[..max] : v;
        }
        bool Bool(string key) => o[key] is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

        switch (Str("type", 64))
        {
            case "ready":
                _vm.RefreshDefaultBrowser();
                break;

            case "load":
                // Sections with live data load when they're opened.
                switch (Str("section", 32))
                {
                    case "privacy":
                        try { await _vm.LoadPermissionsAsync(); await _vm.LoadCookiesAsync(); }
                        catch (Exception ex) { _s.Paths.LogError("Loading privacy settings", ex); }
                        break;
                    case "blocking":
                        _vm.LoadBlocker();
                        break;
                    case "general":
                        _vm.RefreshDefaultBrowser();
                        break;
                }
                break;

            case "set":
                Set(Str("key", 64), o["value"]);
                break;

            case "homepage":
                _vm.HomepageText = Str("text", 2048);
                _vm.CommitHomepage();
                break;

            case "custom-engine":
                _vm.CustomEngineText = Str("text", 2048);
                _vm.CommitCustomEngine();
                break;

            case "accent-hex":
            {
                var text = Str("text", 16).Trim();
                if (!text.StartsWith('#')) text = "#" + text;
                text = text.ToUpperInvariant();
                if (ThemeManager.TryCustom(text) is null) { Toast(tab, "Type a colour like #FF5A1F."); return; }
                _s.Settings.Update(st => st.Accent = text);
                break;
            }

            case "pick-accent":
                PickAccent();
                break;

            case "make-default":
                _vm.MakeDefaultBrowser();
                break;

            case "import-data":
                if (Owner is { } w1) await ImportDialog.ShowAsync(w1, _s);
                break;

            case "import-passwords":
                await _s.Ui.ImportPasswordsAsync();
                break;

            case "open-passwords":
                App.Current.ShowPasswords();
                break;

            case "choose-background":
            {
                var dlg = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp" };
                if (dlg.ShowDialog(Owner) != true) return;
                try { _vm.SetCustomBackground(dlg.FileName); }
                catch (Exception ex) { Toast(tab, ex.Message); }
                break;
            }

            case "clear-background":
                _vm.ClearBackground();
                break;

            case "clear-data":
                if (Owner is { } w2) await ClearDataDialog.ShowAsync(w2, _s);
                await _vm.LoadCookiesAsync();
                await _vm.LoadPermissionsAsync();
                break;

            case "clear-engine-passwords":
                try
                {
                    await ClearDataDialog.ClearAsync(_s, null, history: false, cookies: false, cache: false, downloads: false, autofill: true, permissions: false);
                    Toast(tab, ClearDataDialog.NormalProfile is null ? "Open a page in a normal window and try again to finish." : "Saved passwords removed");
                }
                catch (Exception ex) { Toast(tab, "Couldn't remove passwords: " + ex.Message); }
                break;

            case "refresh-cookies":
                await _vm.LoadCookiesAsync();
                break;

            case "delete-cookies":
            {
                var domain = Str("domain", 255);
                await _vm.DeleteCookiesAsync(domain.Length == 0 ? null : domain);
                break;
            }

            case "permission":
            {
                var row = _vm.Permissions.FirstOrDefault(p => p.Origin == Str("origin", 2048) && p.Kind == Str("kind", 64));
                if (row is null) return;
                var d = Str("action", 16) switch { "allow" => PermissionDecision.Allow, "block" => PermissionDecision.Block, _ => PermissionDecision.Ask };
                await _vm.SetPermissionAsync(row, d);
                break;
            }

            case "update-lists":
                Raise(); // shows "Updating…" right away
                await _vm.UpdateListsAsync();
                break;

            case "filter-list":
            {
                var row = _vm.FilterLists.FirstOrDefault(l => l.List.Id == Str("id", 64));
                if (row is not null) row.IsEnabled = Bool("value"); // saves and rebuilds the filters
                break;
            }

            case "save-rules":
                _vm.CustomRulesText = Str("text", 256 * 1024);
                await _vm.SaveCustomRulesAsync();
                Toast(tab, "Filters saved");
                break;

            case "remove-allowed":
                _vm.RemoveAllowedSite(Str("site", 255));
                break;

            case "open-downloads":
                App.OpenExternal(_vm.DownloadDirectory);
                break;

            case "change-downloads":
            {
                var dlg = new OpenFolderDialog { Title = "Choose where downloads are saved", InitialDirectory = _vm.DownloadDirectory };
                if (dlg.ShowDialog(Owner) == true) _vm.SetDownloadDirectory(dlg.FolderName);
                break;
            }

            case "open-data-folder":
                App.OpenExternal(_vm.DataFolder);
                break;

            case "licenses":
                tab.PostSettingsMessage(new JsonObject { ["type"] = "licenses", ["text"] = _vm.Licenses }.ToJsonString());
                return;

            default:
                return;
        }
        // Everything above may have changed what the page shows.
        await tab.PushSettingsStateAsync();

        void Set(string key, JsonNode? value)
        {
            bool B() => value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            string S() => value is JsonValue v && v.TryGetValue<string>(out var str) ? str : string.Empty;
            double D() => value is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

            switch (key)
            {
                case "startup": if (Enum.TryParse<StartupBehavior>(S(), out var st)) _vm.Startup = st; break;
                case "showHomeButton": _vm.ShowHomeButton = B(); break;
                case "engine": if (_vm.Engines.FirstOrDefault(e => e.Id == S()) is { } eng) _vm.Engine = eng; break;
                case "searchSuggestions": _vm.SearchSuggestions = B(); break;
                case "sleepAfter": if (_vm.SleepOptions.FirstOrDefault(x => x.Minutes == D()) is { } sl) _vm.SleepAfter = sl; break;
                case "discardAfter": if (_vm.DiscardOptions.FirstOrDefault(x => x.Minutes == D()) is { } di) _vm.DiscardAfter = di; break;
                case "tabHoverPreviews": _vm.TabHoverPreviews = B(); break;
                case "themeMode": if (Enum.TryParse<ThemeMode>(S(), out var tm)) _vm.Theme = tm; break;
                case "accent": if (_vm.Accents.FirstOrDefault(a => a.Name == S()) is { } ac) _vm.Accent = ac; break;
                case "tabDensity": if (Enum.TryParse<TabDensity>(S(), out var td)) _vm.TabDensity = td; break;
                case "showTabFavicons": _vm.ShowTabFavicons = B(); break;
                case "sidebarEnabled": _vm.SidebarEnabled = B(); break;
                case "pinnedTabsInSidebar": _vm.PinnedTabsInSidebar = B(); break;
                case "verticalTabs": _s.Settings.Update(x => x.VerticalTabs = B()); break;
                case "interfaceFont": if (_vm.FontChoices.Contains(S())) _vm.InterfaceFont = S(); break;
                case "interfaceFontSize": if (_vm.FontSizes.Contains(D())) _vm.InterfaceFontSize = D(); break;
                case "defaultZoom": if (_vm.Zooms.FirstOrDefault(z => Math.Abs(z.Value - D()) < 0.001) is { } zo) _vm.DefaultZoom = zo; break;
                case "newTabShowClock": _vm.NewTabShowClock = B(); break;
                case "newTabShowRecent": _vm.NewTabShowRecent = B(); break;
                case "newTabShowBookmarks": _vm.NewTabShowBookmarks = B(); break;
                case "offerToSavePasswords": _s.Settings.Update(x => x.OfferToSavePasswords = B()); break;
                case "clearDataOnExit": _vm.ClearDataOnExit = B(); break;
                case "passwordAutosave": _vm.PasswordAutosave = B(); break;
                case "generalAutofill": _vm.GeneralAutofill = B(); break;
                case "privateSearchSuggestions": _vm.PrivateSearchSuggestions = B(); break;
                case "blockerEnabled": _vm.BlockerEnabled = B(); break;
                case "askWhereToSave": _vm.AskWhereToSave = B(); break;
                case "downloadNotifications": _vm.DownloadNotifications = B(); break;
            }
        }
    }

    private static void Toast(BrowserTab tab, string message) =>
        tab.PostSettingsMessage(new JsonObject { ["type"] = "toast", ["message"] = message }.ToJsonString());

    // ---- Windows colour picker -------------------------------------------------------------

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize; public IntPtr hwndOwner, hInstance; public int rgbResult; public IntPtr lpCustColors;
        public int Flags; public IntPtr lCustData, lpfnHook, lpTemplateName;
    }
    [System.Runtime.InteropServices.DllImport("comdlg32.dll")] private static extern bool ChooseColor(ref CHOOSECOLOR cc);
    private static readonly int[] CustomColors = new int[16];

    /// <summary>Windows' standard colour dialog (full palette + RGB/HSL).</summary>
    private void PickAccent()
    {
        var current = ThemeManager.TryCustom(_s.Settings.Current.Accent) ?? ThemeManager.TryCustom(_s.AccentHex) ?? Colors.Teal;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(CustomColors, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = System.Runtime.InteropServices.Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = Owner is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero,
                rgbResult = current.R | (current.G << 8) | (current.B << 16),
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = 0x1 | 0x2, // CC_RGBINIT | CC_FULLOPEN
            };
            if (!ChooseColor(ref cc)) return;
            var hex = $"#{cc.rgbResult & 0xFF:X2}{(cc.rgbResult >> 8) & 0xFF:X2}{(cc.rgbResult >> 16) & 0xFF:X2}";
            _s.Settings.Update(s => s.Accent = hex);
        }
        finally { handle.Free(); }
    }
}
