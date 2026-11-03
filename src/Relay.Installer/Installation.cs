using System.Diagnostics;
using Microsoft.Win32;
using Relay.Core.Setup;

namespace Relay.Installer;

public sealed record InstalledApp(string Directory, string Version);

/// <summary>
/// Per-user installation of RELAY: everything lives under the user's profile and HKEY_CURRENT_USER,
/// so no administrator rights are ever needed.
/// </summary>
public static class Installation
{
    public const string AppName = "RELAY";
    public const string Publisher = "RELAY";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RELAY";
    private const string AppKey = @"Software\RELAY";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RELAY");

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RELAY");

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RELAY.lnk");

    public static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RELAY.lnk");

    public static InstalledApp? FindExisting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var dir = key?.GetValue("InstallLocation") as string;
            var version = key?.GetValue("DisplayVersion") as string;
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Relay.exe"))) return null;
            return new InstalledApp(dir, version ?? "?");
        }
        catch { return null; }
    }

    /// <summary>Registers RELAY in Settings → Apps (per user) and records which shortcuts were made.</summary>
    public static void Register(string installDir, string version, bool startMenu, bool desktop)
    {
        var exe = Path.Combine(installDir, "Relay.exe");
        long sizeKb = 0;
        try { sizeKb = new DirectoryInfo(installDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; } catch { }

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            app.SetValue("InstallDir", installDir);
            app.SetValue("StartMenuShortcut", startMenu ? StartMenuShortcut : string.Empty);
            app.SetValue("DesktopShortcut", desktop ? DesktopShortcut : string.Empty);
        }
        // Listed in Settings → Default apps; becoming the default is the user's choice there.
        DefaultBrowser.Register(exe);
    }

    public static void CreateShortcuts(string installDir, bool startMenu, bool desktop)
    {
        var exe = Path.Combine(installDir, "Relay.exe");
        if (startMenu) ShellLink.Create(StartMenuShortcut, exe, description: "Browse the web with RELAY", appUserModelId: AppIdentity.InstalledAppId);
        else TryDelete(StartMenuShortcut);
        if (desktop) ShellLink.Create(DesktopShortcut, exe, description: "Browse the web with RELAY", appUserModelId: AppIdentity.InstalledAppId);
        else TryDelete(DesktopShortcut);
    }

    /// <summary>RELAY processes started from <paramref name="installDir"/> (other than this one).</summary>
    public static List<Process> RunningCopies(string installDir)
    {
        var result = new List<Process>();
        var me = Environment.ProcessId;
        var dir = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("Relay"))
        {
            if (p.Id == me) continue;
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) result.Add(p);
            }
            catch { /* can't inspect: not ours */ }
        }
        return result;
    }

    /// <summary>Asks running copies to close, then ends them if they don't within the timeout.</summary>
    public static async Task CloseRunningAsync(string installDir, TimeSpan timeout)
    {
        var running = RunningCopies(installDir);
        foreach (var p in running) { try { p.CloseMainWindow(); } catch { } }
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && running.Any(p => { try { return !p.HasExited; } catch { return false; } }))
            await Task.Delay(200);
        foreach (var p in running)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
        await Task.Delay(300);
    }

    /// <summary>
    /// Removes shortcuts, the Apps entry and (optionally) personal data, then deletes the program folder
    /// once this process has exited. Returns false when the folder doesn't look like a RELAY install.
    /// </summary>
    public static bool Uninstall(string installDir, bool removeData)
    {
        var protectedFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        if (!PayloadExtractor.IsSafeToDelete(installDir, protectedFolders)) return false;

        // Shortcuts recorded at install time (fall back to the default locations).
        string? startMenu = null, desktop = null;
        try
        {
            using var app = Registry.CurrentUser.OpenSubKey(AppKey);
            startMenu = app?.GetValue("StartMenuShortcut") as string;
            desktop = app?.GetValue("DesktopShortcut") as string;
        }
        catch { }
        TryDelete(string.IsNullOrEmpty(startMenu) ? StartMenuShortcut : startMenu);
        TryDelete(string.IsNullOrEmpty(desktop) ? DesktopShortcut : desktop);

        DefaultBrowser.Unregister();
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false); } catch { }

        if (removeData && Directory.Exists(DataDirectory))
        {
            try { Directory.Delete(DataDirectory, recursive: true); }
            catch { ScheduleDelete(DataDirectory); }
        }

        ScheduleDelete(Path.GetFullPath(installDir));
        return true;
    }

    /// <summary>Deletes a folder a few seconds after this process exits (a running exe can't delete itself).</summary>
    private static void ScheduleDelete(string dir)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
            // Raw arguments: cmd.exe doesn't understand the escaping ArgumentList would apply.
            // ping is used as a portable "sleep" that works on every Windows version.
            Arguments = $"/d /c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\"",
        };
        try { Process.Start(psi); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
