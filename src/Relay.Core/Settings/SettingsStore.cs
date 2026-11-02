using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Settings;

/// <summary>Loads and atomically saves <see cref="RelaySettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path) => _path = path;

    public RelaySettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public RelaySettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    Current = JsonSerializer.Deserialize<RelaySettings>(File.ReadAllText(_path), Options) ?? new RelaySettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable settings: keep a copy for diagnosis and fall back to defaults.
                try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { /* best effort */ }
                Current = new RelaySettings();
            }
            Normalize(Current);
            return Current;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Applies a change, saves, and notifies listeners.</summary>
    public void Update(Action<RelaySettings> change)
    {
        change(Current);
        Normalize(Current);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Normalize(RelaySettings s)
    {
        if (!RelaySettings.AccentNames.Contains(s.Accent) && !RelaySettings.IsCustomAccent(s.Accent)) s.Accent = "Teal";
        s.DefaultZoom = Math.Clamp(double.IsFinite(s.DefaultZoom) ? s.DefaultZoom : 1, 0.25, 5);
        s.InterfaceFontSize = Math.Clamp(double.IsFinite(s.InterfaceFontSize) ? s.InterfaceFontSize : 13, 11, 16);
        s.WindowWidth = Math.Clamp(double.IsFinite(s.WindowWidth) ? s.WindowWidth : 1280, 640, 10000);
        s.WindowHeight = Math.Clamp(double.IsFinite(s.WindowHeight) ? s.WindowHeight : 820, 420, 10000);
        if (string.IsNullOrWhiteSpace(s.InterfaceFont)) s.InterfaceFont = "Segoe UI Variable Text";
        if (!Uri.TryCreate(s.Homepage, UriKind.Absolute, out _)) s.Homepage = "https://www.wikipedia.org/";
        s.BlockerLists ??= ["easylist", "easyprivacy"];
        s.BlockerAllowedSites ??= [];
        s.BlockerCustomRules ??= string.Empty;
        if (s.BlockerCustomRules.Length > 200_000) s.BlockerCustomRules = s.BlockerCustomRules[..200_000];
    }
}
