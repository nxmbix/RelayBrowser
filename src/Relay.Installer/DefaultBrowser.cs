using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Relay.Installer;

/// <summary>
/// Registers RELAY as a web browser for the current user (no admin rights), so it shows up in Windows' Default apps.
/// Windows 10 and 11 don't let an app make itself the default: the user confirms it in Settings, which
/// <see cref="OpenWindowsSettings"/> opens (on Windows 11, directly on RELAY's page).
/// </summary>
public static class DefaultBrowser
{
    public const string ClientName = "RELAY";
    private const string UrlProgId = "RELAYURL";
    private const string HtmlProgId = "RELAYHTML";
    private const string ClientKey = @"Software\Clients\StartMenuInternet\" + ClientName;
    private const string CapabilitiesKey = ClientKey + @"\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private static readonly string[] FileTypes = [".htm", ".html", ".shtml", ".xht", ".xhtml", ".svg", ".pdf"];
    private static readonly string[] Schemes = ["http", "https"];

    public static void Register(string exe)
    {
        var open = $"\"{exe}\" \"%1\"";
        var icon = $"{exe},0";

        WriteProgId(UrlProgId, "RELAY URL", icon, open, isUrl: true);
        WriteProgId(HtmlProgId, "RELAY HTML Document", icon, open, isUrl: false);

        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue(null, ClientName);
            using (var di = client.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
            using (var cmd = client.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, $"\"{exe}\"");
        }
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", ClientName);
            caps.SetValue("ApplicationDescription", "RELAY — a calm, fast web browser for Windows.");
            caps.SetValue("ApplicationIcon", icon);
            using (var sm = caps.CreateSubKey("StartMenu")) sm.SetValue("StartMenuInternet", ClientName);
            using (var urls = caps.CreateSubKey("URLAssociations"))
                foreach (var s in Schemes) urls.SetValue(s, UrlProgId);
            using (var files = caps.CreateSubKey("FileAssociations"))
                foreach (var ext in FileTypes) files.SetValue(ext, HtmlProgId);
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKey))
            reg.SetValue(ClientName, CapabilitiesKey);
        // "Open with" lists RELAY for these file types.
        foreach (var ext in FileTypes)
        {
            using var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            owp.SetValue(HtmlProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        NotifyShell();
    }

    private static void WriteProgId(string progId, string name, string icon, string open, bool isUrl)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
        key.SetValue(null, name);
        key.SetValue("FriendlyTypeName", name);
        if (isUrl) key.SetValue("URL Protocol", string.Empty);
        using (var app = key.CreateSubKey("Application"))
        {
            app.SetValue("ApplicationName", ClientName);
            app.SetValue("ApplicationIcon", icon);
        }
        using (var di = key.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
        using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, open);
    }

    public static void Unregister()
    {
        void Delete(string path) { try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); } catch { } }
        Delete(ClientKey);
        Delete($@"Software\Classes\{UrlProgId}");
        Delete($@"Software\Classes\{HtmlProgId}");
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsKey, writable: true);
            reg?.DeleteValue(ClientName, throwOnMissingValue: false);
        }
        catch { }
        foreach (var ext in FileTypes)
        {
            try
            {
                using var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                owp?.DeleteValue(HtmlProgId, throwOnMissingValue: false);
            }
            catch { }
        }
        NotifyShell();
    }

    /// <summary>True when the user's https choice (what Windows calls the web browser) is RELAY.</summary>
    public static bool IsDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return string.Equals(key?.GetValue("ProgId") as string, UrlProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Opens Settings → Default apps: on RELAY's own page on Windows 11, the general page on Windows 10.</summary>
    public static void OpenWindowsSettings()
    {
        var uri = Environment.OSVersion.Version.Build >= 22000
            ? $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(ClientName)}"
            : "ms-settings:defaultapps";
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
        }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
