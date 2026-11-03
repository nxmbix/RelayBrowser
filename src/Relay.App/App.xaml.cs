using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Relay.App.Controls;
using Relay.App.Services;
using Relay.App.Themes;
using Relay.App.Views;
using Relay.Browser;
using Relay.Core;
using Relay.Core.Navigation;
using Relay.Core.Settings;
using Relay.Data;

namespace Relay.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TaskManagerWindow? _taskManager;

    public static new App Current => (App)Application.Current;

    public AppPaths Paths { get; private set; } = null!;
    public BrowserServices Services { get; private set; } = null!;
    public ThemeManager Theme { get; private set; } = null!;
    public WindowManager? WindowManager { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await StartAsync(e);
        }
        catch (Exception ex)
        {
            try { Paths?.LogError("Startup", ex); } catch { }
            MessageBox.Show($"RELAY couldn't start.\n\n{ex.GetType().Name}: {ex.Message}", "RELAY", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        var args = CommandLineArgs.Normalize(e.Args, Environment.CommandLine);

        // Settings → Apps → RELAY → Uninstall runs "Relay.exe --uninstall".
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
                await UninstallWindow.UninstallAsync(removeData: false);
            else
                new UninstallWindow().ShowDialog();
            Shutdown();
            return;
        }

        Paths = AppPaths.Resolve(AppContext.BaseDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Paths.LogError("Unhandled", (ex.ExceptionObject as Exception) ?? new Exception("Unknown error"));
        TaskScheduler.UnobservedTaskException += (_, ex) => { Paths.LogError("Unobserved task", ex.Exception); ex.SetObserved(); };

        // One app identity for the process and its shortcuts (set before any window is shown).
        var appId = Relay.Installer.AppIdentity.For(Paths.IsPortable);
        Relay.Installer.AppIdentity.ApplyToProcess(appId);

        // One process per data folder; later launches open a window in the running instance.
        _single = new SingleInstance(Paths.Root);
        if (!_single.IsFirst)
        {
            if (_single.Forward(args)) { Shutdown(); return; }
        }
        if (Environment.ProcessPath is { } exePath)
            _ = Task.Run(() => Relay.Installer.AppIdentity.StampShortcuts(exePath, appId));

        // The Evergreen WebView2 Runtime ships with Windows 10/11. RELAY never installs it silently.
        var runtime = WebViewRuntime.GetInstalledVersion();
        if (runtime is null && Environment.GetEnvironmentVariable("RELAY_DIAGNOSTICS") != "1")
        {
            new RuntimeMissingWindow().ShowDialog();
            Shutdown();
            return;
        }

        var settings = new SettingsStore(Paths.Settings);
        settings.Load();

        RelayDatabase db;
        try
        {
            db = new RelayDatabase(Paths.Database);
            db.Migrate();
        }
        catch (Exception ex)
        {
            Paths.LogError("Opening database", ex);
            MessageBox.Show($"RELAY couldn't open its data file:\n{Paths.Database}\n\n{ex.Message}", "RELAY", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Services = new BrowserServices(Paths, settings, db, new BrowserUi());
        // Start the browser engine while the rest of the UI is being built.
        _ = Services.Environment.GetAsync();
        Theme = new ThemeManager(settings, Services);
        Theme.Apply();
        settings.Changed += (_, _) => Theme.Apply();
        Services.SettingsPage = new SettingsPageService(Services, Theme); // after the theme, so the page gets the new colours

        var firstRun = !settings.Current.SearchEngineChosen;
        // First run: let the user pick a search engine. Nothing is preselected and the order is shuffled.
        if (!settings.Current.SearchEngineChosen)
        {
            var choice = new SearchChoiceWindow();
            if (choice.ShowDialog() == true && choice.SelectedId is { } engineId)
                settings.Update(s => { s.SearchEngineId = engineId; s.SearchEngineChosen = true; });
            // Closed without choosing: RELAY asks again next time it starts.
        }

        WindowManager = new WindowManager(Services);
        _single.Listen(args => Dispatcher.BeginInvoke(() => OpenFromArgs(args)));

        Services.Blocker.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in WindowManager.All)
                foreach (var t in w.ViewModel.Tabs) t.UpdateBlockingFilter();
        });
        _ = Services.Blocker.InitializeAsync();

        try
        {
            await OpenStartupWindowsAsync(args);
        }
        catch (Exception ex)
        {
            Paths.LogError("Opening the first window", ex);
        }

        // Never linger as an invisible process: if no browser window made it onto the screen, say why and quit.
        if (WindowManager.All.Count == 0 || !WindowManager.All.Any(w => w.IsVisible))
        {
            FailStartup("RELAY couldn't open its window.");
            return;
        }
        _ = Services.Downloads.LoadAsync();

        // First run: offer once to copy bookmarks and history from another browser on this PC.
        if (!settings.Current.ImportOffered)
        {
            settings.Update(s => s.ImportOffered = true);
            var owner = WindowManager.All.FirstOrDefault(w => w.IsVisible);
            if (owner is not null && firstRun && BrowserImport.Detect().Count > 0)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = ImportDialog.ShowAsync(owner, Services, firstRun: true)));
        }
    }

    private void FailStartup(string message)
    {
        MessageBox.Show($"{message}\n\nDetails were saved to:\n{Paths.CrashLog}\n\nPlease send that file to the RELAY developer.",
            "RELAY", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private async Task OpenStartupWindowsAsync(string[] args)
    {
        var urls = ParseArgs(args);
        var s = Services.Settings.Current;

        if (s.Startup == StartupBehavior.RestoreSession && !s.ClearDataOnExit)
        {
            try
            {
                var saved = await Services.Sessions.LoadAsync();
                var groups = saved.GroupBy(t => t.WindowIndex).OrderBy(g => g.Key).ToList();
                if (groups.Count > 0)
                {
                    for (var i = 0; i < groups.Count; i++)
                        WindowManager!.Open(false, groups[i], i == groups.Count - 1 ? urls : null);
                    return;
                }
            }
            catch (Exception ex) { Paths.LogError("Restoring session", ex); }
        }

        if (urls.Count == 0 && s.Startup == StartupBehavior.Homepage) urls.Add(s.Homepage);
        WindowManager!.Open(false, urls: urls);
    }

    private void OpenFromArgs(string[] args)
    {
        if (WindowManager is null) return;
        var urls = ParseArgs(args);
        var target = WindowManager.All.LastOrDefault(w => !w.IsPrivate);
        if (urls.Count > 0 && target is not null)
        {
            foreach (var u in urls) target.ViewModel.AddTab(u);
            target.BringToFront();
        }
        else
        {
            WindowManager.Open(false, urls: urls);
        }
    }

    private static List<string> ParseArgs(IEnumerable<string> args)
    {
        var list = new List<string>();
        foreach (var raw in args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("--")).Take(20))
        {
            var a = raw.Trim().Trim('"');
            if (File.Exists(a)) list.Add(new Uri(Path.GetFullPath(a)).AbsoluteUri);
            else if (AddressParser.LooksLikeUrl(a, out var uri) && uri is not null &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile))
                list.Add(uri.AbsoluteUri);
        }
        return list;
    }

    /// <summary>Shows relay://settings (at a section), reusing a tab that already has it open.</summary>
    public void ShowSettings(string? section = null)
    {
        var url = UrlDisplay.SettingsSectionUrl(section);
        var w = WindowManager?.LastActive ?? WindowManager?.Open(false);
        if (w is null) return;
        var vm = w.ViewModel;
        if (vm.Tabs.FirstOrDefault(t => t.IsSettingsPage) is { } existing)
        {
            vm.ActiveTab = existing;
            if (section is not null) existing.Navigate(url);
        }
        else vm.OpenInNewTab(url);
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Shows relay://passwords, reusing a tab that already has it open.</summary>
    public void ShowPasswords()
    {
        var w = WindowManager?.LastActive ?? WindowManager?.Open(false);
        if (w is null) return;
        var vm = w.ViewModel;
        if (vm.Tabs.FirstOrDefault(t => t.IsPasswordsPage) is { } existing) vm.ActiveTab = existing;
        else vm.OpenInNewTab(UrlDisplay.PasswordsUrl);
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void ShowTaskManager()
    {
        if (_taskManager is { IsLoaded: true })
        {
            if (_taskManager.WindowState == WindowState.Minimized) _taskManager.WindowState = WindowState.Normal;
            _taskManager.Activate();
            return;
        }
        _taskManager = new TaskManagerWindow(Services);
        _taskManager.Closed += (_, _) => _taskManager = null;
        _taskManager.Show();
    }

    public void CloseAuxiliaryWindows()
    {
        _taskManager?.Close();
    }

    public static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let one failing component take down every tab.
        Paths.LogError("UI thread", e.Exception);
        e.Handled = true;
        // If the error left RELAY with no visible window, don't keep running invisibly in the background.
        if (WindowManager is { } wm && wm.All.Count > 0 && !Current.Windows.OfType<Window>().Any(w => w.IsVisible))
            Dispatcher.BeginInvoke(() =>
            {
                if (!Current.Windows.OfType<Window>().Any(w => w.IsVisible)) FailStartup("RELAY ran into a problem and has to close.");
            }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services?.Blocker.Shutdown(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
