using System.Text;

namespace Relay.Core.Downloads;

/// <summary>Validates and sanitizes download file names and destination paths.</summary>
public static class FileNameSafety
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windows-invalid characters; kept explicit so behaviour is identical on every OS (and in tests).
    private static readonly char[] InvalidChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0'];

    /// <summary>Types that run code when opened. Downloads of these ask for confirmation.</summary>
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".com", ".scr", ".pif",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".msc", ".jar",
        ".lnk", ".reg", ".dll", ".sys", ".inf", ".application", ".appref-ms", ".gadget", ".msp", ".scf",
        ".url", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".docm", ".xlsm", ".pptm",
        ".appinstaller", ".settingcontent-ms", ".search-ms", ".library-ms", ".msu", ".ocx", ".psd1", ".ps1xml", ".wsc", ".sct", ".xll", ".xlam", ".dotm", ".ppam", ".jnlp", ".diagcab",
    };

    public const int MaxNameLength = 180;

    public static bool IsDangerous(string fileName) =>
        DangerousExtensions.Contains(Path.GetExtension(fileName ?? string.Empty));

    /// <summary>Produces a safe single-segment file name (no directories, no reserved names).</summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        name ??= string.Empty;
        // Strip any directory components, using both separators regardless of platform.
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32 || Array.IndexOf(InvalidChars, c) >= 0) sb.Append('_');
            // Strip bidi override characters that can disguise extensions (e.g. "exe.txt" spoofing).
            else if (c is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‎' or '‏')
                continue;
            else sb.Append(c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        while (result.StartsWith('.')) result = result[1..];
        if (result.Length == 0 || result.All(c => c == '_')) result = fallback;

        var stem = Path.GetFileNameWithoutExtension(result);
        var ext = Path.GetExtension(result);
        if (ReservedNames.Contains(stem)) stem = "_" + stem;

        if (ext.Length > 20) { ext = string.Empty; stem = result; }
        if (stem.Length + ext.Length > MaxNameLength)
            stem = stem[..Math.Max(1, MaxNameLength - ext.Length)];
        return stem + ext;
    }

    /// <summary>
    /// Returns a path inside <paramref name="directory"/> that does not exist yet:
    /// "file.zip", "file (1).zip", "file (2).zip", …
    /// </summary>
    public static string UniquePath(string directory, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var safe = Sanitize(fileName);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var candidate = Path.Combine(directory, safe);
        for (var i = 1; exists(candidate) && i < 10_000; i++)
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
        return candidate;
    }

    /// <summary>True when <paramref name="path"/> resolves to a location inside <paramref name="directory"/>.</summary>
    public static bool IsInside(string directory, string path)
    {
        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
