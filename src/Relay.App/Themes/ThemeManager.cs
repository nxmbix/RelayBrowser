using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Relay.Browser;
using Relay.Core.Settings;
using ThemeMode = Relay.Core.Settings.ThemeMode;

namespace Relay.App.Themes;

/// <summary>
/// Builds the colour tokens (light, dark, private) and swaps them at runtime.
/// Every control references tokens with DynamicResource, so a change applies instantly.
/// </summary>
public sealed class ThemeManager
{
    private readonly SettingsStore _settings;
    private readonly BrowserServices _services;
    private ResourceDictionary? _current;
    private string? _appliedKey;

    public ThemeManager(SettingsStore settings, BrowserServices services)
    {
        _settings = settings;
        _services = services;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public static readonly IReadOnlyDictionary<string, (Color Light, Color Dark)> Accents = new Dictionary<string, (Color, Color)>
    {
        ["Teal"] = (Hex("#1F7A6D"), Hex("#4DB3A2")),
        ["Cobalt"] = (Hex("#2F5FBF"), Hex("#7FA3F2")),
        ["Iris"] = (Hex("#6153C4"), Hex("#A99CF0")),
        ["Amber"] = (Hex("#9C620F"), Hex("#E2AA55")),
        ["Rose"] = (Hex("#B0425A"), Hex("#EE8FA3")),
        ["Graphite"] = (Hex("#46515C"), Hex("#AAB3BD")),
    };

    /// <summary>A custom accent stored as "#RRGGBB" (any colour from the picker).</summary>
    public static Color? TryCustom(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#') return null;
        try { return (Color)ColorConverter.ConvertFromString(value); } catch { return null; }
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public void Apply()
    {
        var s = _settings.Current;
        IsDark = s.Theme switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => SystemPrefersDark() };
        var accent = Accents.TryGetValue(s.Accent, out var a) ? (IsDark ? a.Dark : a.Light)
            : TryCustom(s.Accent) is { } custom ? (IsDark ? Mix(custom, Colors.White, 0.28) : custom)
            : Hex("#1F7A6D");

        // Rebuilding every brush is only needed when something visual changed.
        var key = $"{IsDark}|{s.Accent}|{s.InterfaceFont}|{s.InterfaceFontSize}|{s.TabDensity}";
        if (key == _appliedKey && _current is not null)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _appliedKey = key;

        var dict = Build(IsDark ? DarkPalette : LightPalette, accent, IsDark);
        dict["Font.UI"] = new FontFamily($"{s.InterfaceFont}, Segoe UI Variable Text, Segoe UI");
        dict["Font.Size"] = s.InterfaceFontSize;
        dict["Font.SizeSmall"] = s.InterfaceFontSize - 1.5;
        dict["Tab.Height"] = s.TabDensity == TabDensity.Compact ? 30.0 : 34.0;
        dict["Tab.MaxWidth"] = s.TabDensity == TabDensity.Compact ? 200.0 : 236.0;
        dict["Strip.Height"] = s.TabDensity == TabDensity.Compact ? 38.0 : 44.0;

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;

        _services.IsDarkTheme = IsDark;
        var accentForPage = Accents.TryGetValue(s.Accent, out var pa) ? pa.Light : TryCustom(s.Accent) ?? Hex("#1F7A6D");
        _services.AccentHex = $"#{accentForPage.R:X2}{accentForPage.G:X2}{accentForPage.B:X2}";
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        _services.NewTab.RaiseStateChanged();
    }

    /// <summary>Private windows always use a distinct deep-violet palette, set on the window itself.</summary>
    public static void ApplyPrivatePalette(FrameworkElement window)
    {
        var d = Build(PrivatePalette, Hex("#A48BF0"), isDark: true);
        foreach (var key in d.Keys) window.Resources[key] = d[key];
    }

    private sealed record Palette(
        string Chrome, string Toolbar, string Surface, string SurfaceAlt, string Hover, string Pressed,
        string Border, string BorderStrong, string Text, string TextSecondary, string TextTertiary,
        string Field, string FieldFocus, string Danger, string Scrim, string TabActive, string Shadow);

    private static readonly Palette LightPalette = new(
        Chrome: "#E8EBED", Toolbar: "#FBFBFC", Surface: "#FFFFFF", SurfaceAlt: "#F3F5F6",
        Hover: "#14000000", Pressed: "#22000000", Border: "#1A1B1F23", BorderStrong: "#2E1B1F23",
        Text: "#1B1F23", TextSecondary: "#555E66", TextTertiary: "#868E96",
        Field: "#EEF0F2", FieldFocus: "#FFFFFF", Danger: "#C4443B", Scrim: "#40000000", TabActive: "#FBFBFC", Shadow: "#1F000000");

    private static readonly Palette DarkPalette = new(
        Chrome: "#17181A", Toolbar: "#232528", Surface: "#26282B", SurfaceAlt: "#2D3034",
        Hover: "#17FFFFFF", Pressed: "#24FFFFFF", Border: "#1CFFFFFF", BorderStrong: "#30FFFFFF",
        Text: "#E8EAED", TextSecondary: "#A6ACB2", TextTertiary: "#7C838A",
        Field: "#1B1D1F", FieldFocus: "#16181A", Danger: "#F07A70", Scrim: "#66000000", TabActive: "#232528", Shadow: "#66000000");

    private static readonly Palette PrivatePalette = new(
        Chrome: "#1A1720", Toolbar: "#26222E", Surface: "#2A2632", SurfaceAlt: "#322D3B",
        Hover: "#17FFFFFF", Pressed: "#24FFFFFF", Border: "#1EFFFFFF", BorderStrong: "#33FFFFFF",
        Text: "#ECEAF0", TextSecondary: "#ADA7B8", TextTertiary: "#85808F",
        Field: "#1D1A23", FieldFocus: "#18161D", Danger: "#F07A70", Scrim: "#66000000", TabActive: "#26222E", Shadow: "#66000000");

    private static ResourceDictionary Build(Palette p, Color accent, bool isDark)
    {
        var d = new ResourceDictionary();
        void Brush(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); d[key] = b; d[key.Replace("Brush.", "Color.")] = c; }

        Brush("Brush.Chrome", Hex(p.Chrome));
        Brush("Brush.Toolbar", Hex(p.Toolbar));
        Brush("Brush.TabActive", Hex(p.TabActive));
        Brush("Brush.Surface", Hex(p.Surface));
        Brush("Brush.SurfaceAlt", Hex(p.SurfaceAlt));
        Brush("Brush.Hover", Hex(p.Hover));
        Brush("Brush.Pressed", Hex(p.Pressed));
        Brush("Brush.Border", Hex(p.Border));
        Brush("Brush.BorderStrong", Hex(p.BorderStrong));
        Brush("Brush.Text", Hex(p.Text));
        Brush("Brush.TextSecondary", Hex(p.TextSecondary));
        Brush("Brush.TextTertiary", Hex(p.TextTertiary));
        Brush("Brush.Field", Hex(p.Field));
        Brush("Brush.FieldFocus", Hex(p.FieldFocus));
        Brush("Brush.Danger", Hex(p.Danger));
        Brush("Brush.DangerText", isDark ? Hex("#111315") : Colors.White);
        Brush("Brush.Neutral", isDark ? Hex("#3A3D42") : Hex("#E4E7EA"));
        Brush("Brush.DangerSubtle", Color.FromArgb(isDark ? (byte)46 : (byte)26, Hex(p.Danger).R, Hex(p.Danger).G, Hex(p.Danger).B));
        Brush("Brush.Scrim", Hex(p.Scrim));
        Brush("Brush.Shadow", Hex(p.Shadow));
        Brush("Brush.Accent", accent);
        Brush("Brush.AccentHover", Mix(accent, isDark ? Colors.White : Colors.Black, 0.12));
        Brush("Brush.AccentText", isDark ? Hex("#111315") : Colors.White);
        Brush("Brush.AccentSubtle", Color.FromArgb(isDark ? (byte)46 : (byte)30, accent.R, accent.G, accent.B));
        Brush("Brush.Selection", Color.FromArgb(isDark ? (byte)90 : (byte)60, accent.R, accent.G, accent.B));
        d["Shadow.Color"] = Hex(p.Shadow);
        d["Theme.IsDark"] = isDark;
        return d;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
