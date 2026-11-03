using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Relay.Installer;

/// <summary>Creates .lnk shortcuts through the Windows shell (IShellLinkW) — no scripting host needed.</summary>
public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    private const ushort VT_LPWSTR = 31;
    private const int STGM_READWRITE = 2;

    // PKEY_AppUserModel_ID
    private static PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);

    private static string? GetAppUserModelId(IShellLinkW link)
    {
        var store = (IPropertyStore)link;
        store.GetValue(ref AppUserModelIdKey, out var pv);
        try { return pv.VarType == VT_LPWSTR ? Marshal.PtrToStringUni(pv.Pointer) : null; }
        finally { PropVariantClear(ref pv); }
    }

    private static void SetAppUserModelId(IShellLinkW link, string appId)
    {
        var store = (IPropertyStore)link;
        var pv = new PropVariant { VarType = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            store.SetValue(ref AppUserModelIdKey, ref pv);
            store.Commit();
        }
        finally { PropVariantClear(ref pv); }
    }

    /// <summary>Sets the AppUserModelID of an existing shortcut, if it starts <paramref name="target"/> and doesn't have it yet.</summary>
    public static bool TryStampAppUserModelId(string shortcutPath, string target, string appId)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, STGM_READWRITE);
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            if (path.Length == 0 || !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return false;
            if (GetAppUserModelId(link) == appId) return false;
            SetAppUserModelId(link, appId);
            ((IPersistFile)link).Save(null, true);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void Create(string shortcutPath, string target, string? arguments = null, string? description = null, string? appUserModelId = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
            link.SetIconLocation(target, 0);
            if (!string.IsNullOrEmpty(appUserModelId)) SetAppUserModelId(link, appUserModelId);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }
}
