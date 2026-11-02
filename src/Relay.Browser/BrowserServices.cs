using Relay.Core;
using Relay.Core.Search;
using Relay.Core.Settings;
using Relay.Data;

namespace Relay.Browser;

/// <summary>Everything a tab needs, created once at startup and shared by all windows.</summary>
public sealed class BrowserServices
{
    public BrowserServices(AppPaths paths, SettingsStore settings, RelayDatabase db, IBrowserUi ui)
    {
        Paths = paths;
        Settings = settings;
        Ui = ui;
        History = new HistoryRepository(db);
        Bookmarks = new BookmarkRepository(db);
        QuickLinks = new QuickLinkRepository(db);
        ReadingList = new ReadingListRepository(db);
        DownloadsRepo = new DownloadRepository(db);
        Permissions = new PermissionRepository(db);
        Favicons = new FaviconRepository(db);
        Sessions = new SessionRepository(db);
        Passwords = new PasswordRepository(db, new DpapiProtector());
        Environment = new BrowserEnvironment(paths);
        Downloads = new DownloadManager(this);
        PermissionHandler = new PermissionHandler(this);
        NewTab = new NewTabService(this);
        FaviconCache = new FaviconCache(Favicons);
        Blocker = new BlockerService(this);
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public IBrowserUi Ui { get; }
    public HistoryRepository History { get; }
    public BookmarkRepository Bookmarks { get; }
    public QuickLinkRepository QuickLinks { get; }
    public ReadingListRepository ReadingList { get; }
    public DownloadRepository DownloadsRepo { get; }
    public PermissionRepository Permissions { get; }
    public FaviconRepository Favicons { get; }
    public SessionRepository Sessions { get; }
    public PasswordRepository Passwords { get; }
    public BrowserEnvironment Environment { get; }
    public DownloadManager Downloads { get; }
    public PermissionHandler PermissionHandler { get; }
    public NewTabService NewTab { get; }
    public FaviconCache FaviconCache { get; }
    public BlockerService Blocker { get; }

    /// <summary>Whether the UI is currently dark. Set by the theme manager.</summary>
    public bool IsDarkTheme { get; set; }

    /// <summary>The accent colour as #RRGGBB, used by the new tab page.</summary>
    public string AccentHex { get; set; } = "#1F7A6D";

    public SearchEngine SearchEngine => SearchEngines.Resolve(Settings.Current.SearchEngineId, Settings.Current.CustomSearchTemplate);

    public string DownloadDirectory
    {
        get
        {
            var configured = Settings.Current.DownloadDirectory;
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
            var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var dl = Path.Combine(userProfile, "Downloads");
            return Directory.Exists(dl) ? dl : userProfile;
        }
    }

    public static string NewTabFolder => Path.Combine(AppContext.BaseDirectory, "NewTab");
}
