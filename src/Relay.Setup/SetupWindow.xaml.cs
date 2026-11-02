using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Relay.Core.Setup;
using Relay.Installer;

namespace Relay.Setup;

/// <summary>RELAY's own setup wizard: welcome → (options) → installing → done. Per-user, no admin rights.</summary>
public partial class SetupWindow : Window
{
    private const long FallbackSizeBytes = 150L * 1024 * 1024;
    private static readonly string[] StepNames = ["Welcome", "Options", "Install", "Done"];

    private readonly string _version;
    private readonly InstalledApp? _existing;
    private long _payloadBytes = FallbackSizeBytes;
    private CancellationTokenSource? _cts;
    private bool _installing;
    private string _installedDir = string.Empty;

    public SetupWindow()
    {
        InitializeComponent();
        _version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                   ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        VersionText.Text = $"Version {_version}";
        _existing = Installation.FindExisting();
        LocationBox.Text = _existing?.Directory ?? Installation.DefaultDirectory;

        if (_existing is not null)
        {
            WelcomeTitle.Text = "Update RELAY";
            WelcomeInstall.Content = "Update";
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateText.Text = _existing.Version == _version
                ? $"RELAY {_version} is already installed. Continuing reinstalls it — your bookmarks, history and settings stay as they are."
                : $"RELAY {_existing.Version} is installed. This updates it to {_version} — your bookmarks, history and settings stay as they are.";
            // Keep the user's earlier shortcut choices on update.
            StartMenuCheck.IsChecked = File.Exists(Installation.StartMenuShortcut);
            DesktopCheck.IsChecked = File.Exists(Installation.DesktopShortcut);
        }

        try
        {
            using var payload = OpenPayload();
            if (payload is not null) _payloadBytes = PayloadExtractor.Measure(payload).Bytes;
        }
        catch { /* measured again during install */ }

        BuildSteps(0);
        UpdateSpace();
        SizeChanged += (_, _) => ClipRounded();
        Loaded += (_, _) => { ClipRounded(); FadeIn(PageWelcome); WelcomeInstall.Focus(); };
    }

    private static Stream? OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

    // ---- Chrome --------------------------------------------------------------------------

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_installing) { OnCancel(sender, e); return; }
        Close();
    }

    private void ClipRounded()
    {
        // Keep the brand panel's artwork inside the window's rounded corners.
        if (Content is Border outer && outer.Child is Grid g && g.Children[0] is Border brand)
            brand.Clip = new RectangleGeometry(new Rect(0, 0, brand.ActualWidth + 20, brand.ActualHeight), 18, 18);
    }

    // ---- Steps & pages ---------------------------------------------------------------------

    private void BuildSteps(int active)
    {
        Steps.Children.Clear();
        for (var i = 0; i < StepNames.Length; i++)
        {
            var done = i < active;
            var current = i == active;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var dot = new Grid { Width = 26, Height = 26 };
            dot.Children.Add(new Ellipse
            {
                Fill = current ? Brushes.White : done ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromArgb(current || done ? (byte)0 : (byte)0x77, 255, 255, 255)),
                StrokeThickness = 1.5,
            });
            dot.Children.Add(new TextBlock
            {
                Text = done ? "" : (i + 1).ToString(),
                FontFamily = done ? (FontFamily)FindResource("Icons") : (FontFamily)FindResource("Text"),
                FontSize = done ? 11 : 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = current ? (Brush)FindResource("Teal") : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = StepNames[i],
                Foreground = Brushes.White,
                Opacity = current ? 1 : 0.62,
                FontSize = 14,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Steps.Children.Add(row);
        }
    }

    private FrameworkElement? _currentPage;

    private void Show(FrameworkElement page, int step)
    {
        foreach (FrameworkElement p in Pages.Children) p.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        _currentPage = page;
        BuildSteps(step);
        FadeIn(page);
    }

    private static void FadeIn(FrameworkElement page)
    {
        var shift = new TranslateTransform(18, 0);
        page.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void OnShowOptions(object sender, RoutedEventArgs e) => Show(PageOptions, 1);
    private void OnBackToWelcome(object sender, RoutedEventArgs e) => Show(PageWelcome, 0);

    // ---- Options ---------------------------------------------------------------------------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to install RELAY" };
        try
        {
            var current = LocationBox.Text.Trim();
            var parent = Directory.Exists(current) ? current : System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) dlg.InitialDirectory = parent;
        }
        catch { }
        if (dlg.ShowDialog(this) != true) return;
        var chosen = dlg.FolderName;
        // Install into a RELAY subfolder unless the user picked one already.
        if (!System.IO.Path.GetFileName(chosen.TrimEnd('\\')).Equals("RELAY", StringComparison.OrdinalIgnoreCase))
            chosen = System.IO.Path.Combine(chosen, "RELAY");
        LocationBox.Text = chosen;
    }

    private void OnLocationChanged(object sender, TextChangedEventArgs e) => UpdateSpace();

    private void UpdateSpace()
    {
        if (SpaceText is null) return;
        var needed = FormatBytes(_payloadBytes);
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(LocationBox.Text));
            var drive = new DriveInfo(root!);
            SpaceText.Text = $"Needs about {needed} · {FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}";
        }
        catch
        {
            SpaceText.Text = $"Needs about {needed}";
        }
    }

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : $"{Math.Max(1, b / (1L << 20))} MB";

    // ---- Install ---------------------------------------------------------------------------

    private string TargetDir => System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(LocationBox.Text.Trim()));

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        string dir;
        try { dir = TargetDir; }
        catch
        {
            Show(PageOptions, 1);
            SpaceText.Text = "That location isn't a valid folder path.";
            return;
        }

        if (Installation.RunningCopies(dir).Count > 0)
        {
            Show(PageRunning, 2);
            return;
        }
        await RunInstallAsync(dir);
    }

    private async void OnCloseRunningAndInstall(object sender, RoutedEventArgs e)
    {
        var dir = TargetDir;
        Show(PageProgress, 2);
        StatusText.Text = "Closing RELAY…";
        await Installation.CloseRunningAsync(dir, TimeSpan.FromSeconds(6));
        await RunInstallAsync(dir);
    }

    private async Task RunInstallAsync(string dir)
    {
        var isUpdate = _existing is not null;
        var wasThere = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        var startMenu = StartMenuCheck.IsChecked == true;
        var desktop = DesktopCheck.IsChecked == true;

        _installing = true;
        _cts = new CancellationTokenSource();
        ProgressTitle.Text = isUpdate ? "Updating RELAY" : "Installing RELAY";
        CancelButton.IsEnabled = true;
        Progress.Value = 0;
        PercentText.Text = "0%";
        StatusText.Text = "Preparing…";
        FileText.Text = string.Empty;
        Show(PageProgress, 2);

        var progress = new Progress<ExtractProgress>(p =>
        {
            var fraction = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : (double)p.Done / Math.Max(1, p.Total);
            // Leave the last few percent for shortcuts and registration.
            var shown = fraction * 0.94;
            Progress.Value = shown;
            PercentText.Text = $"{Math.Floor(shown * 100)}%";
            StatusText.Text = $"Copying files · {p.Done} of {p.Total}";
            FileText.Text = p.Current;
        });

        try
        {
            using var payload = OpenPayload() ?? throw new InvalidOperationException(
                "This copy of RELAY Setup doesn't contain the RELAY program. Download Setup again.");
            var token = _cts.Token;
            await Task.Run(() => PayloadExtractor.Extract(payload, dir, progress, token), token);

            CancelButton.IsEnabled = false;
            StatusText.Text = "Adding shortcuts…";
            FileText.Text = string.Empty;
            await Task.Run(() =>
            {
                Installation.CreateShortcuts(dir, startMenu, desktop);
                Installation.Register(dir, _version, startMenu, desktop);
            });

            Progress.Value = 1;
            PercentText.Text = "100%";
            _installedDir = dir;
            _installing = false;
            await Task.Delay(350);
            ShowDone(isUpdate);
        }
        catch (OperationCanceledException)
        {
            _installing = false;
            if (!wasThere) TryRemove(dir); // leave nothing half-installed behind
            Show(PageWelcome, 0);
        }
        catch (Exception ex)
        {
            _installing = false;
            if (!wasThere) TryRemove(dir);
            ErrorText.Text = ex switch
            {
                UnauthorizedAccessException => "Setup isn't allowed to write to that folder. Choose a folder inside your user account, such as the default location.",
                IOException io when io.HResult == unchecked((int)0x80070070) => "There isn't enough free space on that drive.",
                IOException => "A file couldn't be written — it may be in use. Close RELAY (and any program using its folder) and try again.",
                _ => "Something unexpected went wrong while installing.",
            };
            ErrorDetail.Text = ex.Message;
            Show(PageError, 2);
        }
    }

    private static void TryRemove(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling…";
        _cts?.Cancel();
    }

    // ---- Done ------------------------------------------------------------------------------

    private void ShowDone(bool isUpdate)
    {
        DoneTitle.Text = isUpdate ? "RELAY is up to date" : "RELAY is ready";
        RuntimeNote.Visibility = HasWebView2Runtime() ? Visibility.Collapsed : Visibility.Visible;
        Show(PageDone, 3);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        OpenButton.Focus();
    }

    private void OnOpenRelay(object sender, RoutedEventArgs e)
    {
        Launch();
        Close();
    }

    private void OnFinishClose(object sender, RoutedEventArgs e) => Close();

    private void Launch()
    {
        try
        {
            var exe = System.IO.Path.Combine(_installedDir, "Relay.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _installedDir });
        }
        catch { /* the user can start it from the Start menu */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            OnCancel(this, new RoutedEventArgs());
            return;
        }
        base.OnClosing(e);
    }

    private void OnGetRuntime(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); } catch { }
    }

    /// <summary>The Evergreen WebView2 Runtime registers its version under EdgeUpdate (per machine or per user).</summary>
    private static bool HasWebView2Runtime()
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        string?[] values =
        [
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\" + client)?.GetValue("pv") as string,
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\" + client)?.GetValue("pv") as string,
            Registry.CurrentUser.OpenSubKey(@"Software\" + client)?.GetValue("pv") as string,
        ];
        return values.Any(v => !string.IsNullOrEmpty(v) && v != "0.0.0.0");
    }
}
