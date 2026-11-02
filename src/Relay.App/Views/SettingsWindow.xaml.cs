using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Relay.App.Controls;
using Relay.App.ViewModels;
using Relay.Browser;
using Relay.Core.Models;

namespace Relay.App.Views;

public partial class SettingsWindow : Window
{
    private readonly BrowserServices _s;
    private readonly SettingsViewModel _vm;

    public SettingsWindow(BrowserServices services, string? section)
    {
        _s = services;
        _vm = new SettingsViewModel(services);
        DataContext = _vm;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this, App.Current.Theme.IsDark);
        App.Current.Theme.ThemeChanged += OnThemeChanged;
        StateChanged += (_, _) => RootBorder.Padding = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        if (App.Current.WindowManager?.LastActive is { IsVisible: true } owner)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
            Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Navigate(section);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => NativeMethods.StyleWindow(this, App.Current.Theme.IsDark);

    protected override void OnClosed(EventArgs e)
    {
        App.Current.Theme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    public void Navigate(string? section)
    {
        var tag = string.IsNullOrEmpty(section) ? "general" : section;
        var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? (ListBoxItem)Nav.Items[0];
        Nav.SelectedItem = item;
    }

    private async void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "general";
        SectionGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        SectionAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SectionPrivacy.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        SectionDownloads.Visibility = tag == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        SectionBlocking.Visibility = tag == "blocking" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "blocking") _vm.LoadBlocker();
        SectionAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        if (tag == "privacy")
        {
            try
            {
                await _vm.LoadPermissionsAsync();
                await _vm.LoadCookiesAsync();
            }
            catch (Exception ex) { _s.Paths.LogError("Loading privacy settings", ex); }
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSaveHomepage(object sender, RoutedEventArgs e) => _vm.CommitHomepage();
    private void OnHomepageKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) _vm.CommitHomepage(); }
    private void OnSaveCustomEngine(object sender, RoutedEventArgs e) => _vm.CommitCustomEngine();

    private void OnChooseBackground(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.SetCustomBackground(dlg.FileName); }
        catch (Exception ex) { RelayDialog.Info(this, "Couldn't use that image", ex.Message, Icons.Warning); }
    }

    private void OnClearBackground(object sender, RoutedEventArgs e) => _vm.ClearBackground();

    private async void OnClearData(object sender, RoutedEventArgs e)
    {
        await ClearDataDialog.ShowAsync(this, _s);
        await _vm.LoadCookiesAsync();
        await _vm.LoadPermissionsAsync();
    }

    private async void OnClearPasswords(object sender, RoutedEventArgs e)
    {
        if (!RelayDialog.Confirm(this, "Remove all saved passwords?", "Passwords the browser engine saved for websites will be deleted. You'll need to type them again.", "Remove", danger: true)) return;
        try
        {
            await ClearDataDialog.ClearAsync(_s, null, history: false, cookies: false, cache: false, downloads: false, autofill: true, permissions: false);
            RelayDialog.Info(this, "Saved passwords removed", ClearDataDialog.NormalProfile is null
                ? "Open a page in a normal window and try again to finish clearing them." : "Done.", Icons.Check);
        }
        catch (Exception ex) { RelayDialog.Info(this, "Couldn't remove passwords", ex.Message, Icons.Warning); }
    }

    private async void OnRefreshCookies(object sender, RoutedEventArgs e) => await _vm.LoadCookiesAsync();

    private async void OnDeleteAllCookies(object sender, RoutedEventArgs e)
    {
        if (!RelayDialog.Confirm(this, "Delete all cookies?", "You'll be signed out of most websites.", "Delete all", danger: true)) return;
        await _vm.DeleteCookiesAsync(null);
    }

    private async void OnDeleteSiteCookies(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CookieSiteRow row) await _vm.DeleteCookiesAsync(row.Domain);
    }

    private async void OnPermissionChange(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SitePermissionRow row, Tag: string action }) return;
        var d = action switch { "allow" => PermissionDecision.Allow, "block" => PermissionDecision.Block, _ => PermissionDecision.Ask };
        await _vm.SetPermissionAsync(row, d);
    }

    private async void OnUpdateLists(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) b.IsEnabled = false;
        try { await _vm.UpdateListsAsync(); }
        finally { if (sender is Button b2) b2.IsEnabled = true; }
    }

    private async void OnSaveCustomRules(object sender, RoutedEventArgs e) => await _vm.SaveCustomRulesAsync();

    private void OnRemoveAllowedSite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string site) _vm.RemoveAllowedSite(site);
    }

    private void OnOpenDownloadFolder(object sender, RoutedEventArgs e) => App.OpenExternal(_vm.DownloadDirectory);

    private void OnChangeDownloadFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where downloads are saved", InitialDirectory = _vm.DownloadDirectory };
        if (dlg.ShowDialog(this) == true) _vm.SetDownloadDirectory(dlg.FolderName);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => App.OpenExternal(_vm.DataFolder);
}
