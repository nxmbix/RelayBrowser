using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Relay.Core.Navigation;
using Relay.Data;

namespace Relay.Browser;

/// <summary>In-memory cache of decoded favicons keyed by host, backed by SQLite.</summary>
public sealed class FaviconCache(FaviconRepository repo)
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImageSource?> GetForUrlAsync(string? url)
    {
        var host = UrlDisplay.Host(url);
        if (host.Length == 0) return null;
        if (_cache.TryGetValue(host, out var cached)) return cached;
        byte[]? bytes = null;
        try { bytes = await repo.GetAsync(host); } catch { /* cache is optional */ }
        var img = bytes is null ? null : Decode(bytes);
        _cache[host] = img;
        return img;
    }

    public void Put(string? url, ImageSource image)
    {
        var host = UrlDisplay.Host(url);
        if (host.Length > 0) _cache[host] = image;
    }

    public void Clear() => _cache.Clear();

    public static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
