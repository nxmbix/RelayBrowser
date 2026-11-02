using System.Runtime.InteropServices;

namespace Relay.Installer;

/// <summary>
/// RELAY's Application User Model ID. The process and every RELAY shortcut carry the same id, so windows, pinned
/// taskbar icons and Start menu entries are one app to the shell. (Task Manager groups by process tree, not by this id.)
/// </summary>
public static class AppIdentity
{
    public const string InstalledAppId = "RELAY.Browser";
    public const string PortableAppId = "RELAY.Browser.Portable";

    public static string For(bool portable) => portable ? PortableAppId : InstalledAppId;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Must run before the first window is shown.</summary>
    public static void ApplyToProcess(string appId)
    {
        try { SetCurrentProcessExplicitAppUserModelID(appId); } catch { /* shell unavailable */ }
    }

    /// <summary>
    /// Writes the id into existing shortcuts that start <paramref name="exe"/> (Start menu, desktop, taskbar pins),
    /// so icons pinned before this version stay merged with the running window. Shortcuts already stamped are not rewritten.
    /// </summary>
    public static void StampShortcuts(string exe, string appId)
    {
        var candidates = new List<string> { Installation.StartMenuShortcut, Installation.DesktopShortcut };
        try
        {
            var pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
            if (Directory.Exists(pins)) candidates.AddRange(Directory.EnumerateFiles(pins, "*.lnk"));
        }
        catch { }
        foreach (var lnk in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(lnk)) ShellLink.TryStampAppUserModelId(lnk, exe, appId); } catch { }
        }
    }
}
