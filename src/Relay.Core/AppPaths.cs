namespace Relay.Core;

/// <summary>
/// Resolves where RELAY keeps its data. Everything lives in one user-writable folder:
/// <list type="bullet">
/// <item>Portable mode: a "Data" folder next to the executable, when a file named
/// <c>relay.portable</c> sits beside the exe and that folder is writable.</item>
/// <item>Otherwise: %LOCALAPPDATA%\RELAY.</item>
/// </list>
/// Nothing is written to the registry or to machine-wide locations.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root, bool isPortable)
    {
        Root = root;
        IsPortable = isPortable;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WebProfile);
        Directory.CreateDirectory(Backgrounds);
    }

    public string Root { get; }
    public bool IsPortable { get; }
    public string Database => Path.Combine(Root, "relay.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string WebProfile => Path.Combine(Root, "WebView2");
    public string Backgrounds => Path.Combine(Root, "Backgrounds");
    public string CrashLog => Path.Combine(Root, "relay-errors.log");

    public static AppPaths Resolve(string exeDirectory)
    {
        var marker = Path.Combine(exeDirectory, "relay.portable");
        if (File.Exists(marker))
        {
            var portableRoot = Path.Combine(exeDirectory, "Data");
            if (CanWrite(portableRoot)) return new AppPaths(portableRoot, true);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return new AppPaths(Path.Combine(local, "RELAY"), false);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public void LogError(string context, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
