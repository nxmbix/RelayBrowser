using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Relay.App.Controls;

/// <summary>The icon and type name Windows shows for a file (an .exe's own icon, a PDF's reader icon, …).</summary>
public static class FileIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Icon and type for <paramref name="path"/>. When the file doesn't exist yet (a download that hasn't been kept),
    /// Windows answers from the extension alone.
    /// </summary>
    public static (ImageSource? Icon, string TypeName) Get(string path)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_TYPENAME;
        if (!File.Exists(path)) flags |= SHGFI_USEFILEATTRIBUTES;
        try
        {
            if (SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero)
                return (null, string.Empty);
            ImageSource? icon = null;
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    icon = src;
                }
                finally { DestroyIcon(info.hIcon); }
            }
            return (icon, info.szTypeName ?? string.Empty);
        }
        catch { return (null, string.Empty); }
    }

    /// <summary>"119 MB", "840 KB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
