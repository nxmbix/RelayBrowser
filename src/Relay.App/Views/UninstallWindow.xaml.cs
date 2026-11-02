using System.Windows;
using System.Windows.Input;
using Relay.Installer;

namespace Relay.App.Views;

/// <summary>Shown by "Relay.exe --uninstall" (Settings → Apps → RELAY → Uninstall).</summary>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
    }

    private static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Runs the uninstall. Returns false when this copy isn't an installed RELAY.</summary>
    public static async Task<bool> UninstallAsync(bool removeData)
    {
        // Only a copy installed by RELAY Setup (registered at exactly this folder) removes itself.
        var registered = Installation.FindExisting()?.Directory?.TrimEnd('\\', '/');
        if (registered is null || !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
            return false;
        if (File.Exists(Path.Combine(InstallDir, "relay.portable"))) return false;

        await Installation.CloseRunningAsync(InstallDir, TimeSpan.FromSeconds(5));
        return Installation.Uninstall(InstallDir, removeData);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            try { DragMove(); } catch { }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        RemoveButton.IsEnabled = false;
        RemoveButton.Content = "Removing…";
        var ok = await UninstallAsync(RemoveDataCheck.IsChecked == true);
        ConfirmPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        if (!ok)
        {
            DoneTitle.Text = "Nothing to remove here";
            DoneText.Text = "This copy of RELAY wasn't installed by RELAY Setup (it may be the portable version). To remove a portable copy, just delete its folder.";
        }
    }
}
