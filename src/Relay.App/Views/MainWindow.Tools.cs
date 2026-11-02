using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;
using Relay.App.Controls;
using Relay.App.ViewModels;
using Relay.Browser;
using Relay.Core.Downloads;

namespace Relay.App.Views;

/// <summary>Page tools: reader view, screenshots, picture-in-picture, QR codes and the small notice ("toast") they use.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    // ---- Toast ----------------------------------------------------------------------------

    private void ShowToast(string glyph, string title, string text, params (string Label, Action Run)[] actions)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        ToastGlyph.Text = glyph;
        ToastTitle.Text = title;
        ToastText.Text = text;
        ToastText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        ToastActions.Children.Clear();
        foreach (var (label, run) in actions)
        {
            var b = new Button { Content = label, Style = (Style)FindResource("Button.Link"), Margin = new Thickness(0, 0, 16, 0) };
            b.Click += (_, _) => { ToastPopup.IsOpen = false; run(); };
            ToastActions.Children.Add(b);
        }
        ToastActions.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToastPopup.HorizontalOffset = Math.Max(0, RootBorder.ActualWidth - 368);
        ToastPopup.VerticalOffset = Math.Max(0, Toolbar.ActualHeight + StripRow.ActualHeight + 4);
        ToastPopup.IsOpen = true;
        _toastTimer.Stop();
        if (_toastTimer.Tag is null)
        {
            _toastTimer.Tag = true;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (!ToastPopup.IsMouseOver) ToastPopup.IsOpen = false; else _toastTimer.Start(); };
        }
        _toastTimer.Start();
    }

    private void OnToastClose(object sender, RoutedEventArgs e) => ToastPopup.IsOpen = false;

    // ---- Reader view ------------------------------------------------------------------------

    private void OnReaderClick(object sender, RoutedEventArgs e) => _ = ToggleReaderAsync();

    private async Task ToggleReaderAsync()
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null) return;
        if (tab.IsReaderPage) { tab.ExitReader(); return; }
        if (!await tab.EnterReaderAsync())
            ShowToast(Icons.Info, "Reader view isn't available here", "RELAY couldn't find an article on this page.");
    }

    // ---- Screenshots ----------------------------------------------------------------------

    private async Task TakeScreenshotAsync(bool fullPage)
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null || !tab.IsCreated) return;
        var bytes = await tab.CaptureScreenshotAsync(fullPage);
        if (bytes is null || bytes.Length == 0)
        {
            ShowToast(Icons.Warning, "Couldn't take a screenshot", "Try again once the page has finished loading.");
            return;
        }
        try
        {
            var dir = _s.DownloadDirectory;
            var name = FileNameSafety.Sanitize($"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss} {tab.Host}.png");
            var path = FileNameSafety.UniquePath(dir, name);
            await File.WriteAllBytesAsync(path, bytes);
            try { Clipboard.SetImage(Decode(bytes)); } catch { /* clipboard busy */ }
            ShowToast("", "Screenshot saved and copied", Path.GetFileName(path),
                ("Open", () => App.OpenExternal(path)),
                ("Show in folder", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } })));
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Saving screenshot", ex);
            ShowToast(Icons.Warning, "Couldn't save the screenshot", ex.Message);
        }
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void ShowScreenshotMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Visible area", "", () => _ = TakeScreenshotAsync(false)));
        menu.Items.Add(Item("Full page", "", () => _ = TakeScreenshotAsync(true)));
        menu.IsOpen = true;
    }

    // ---- Picture-in-picture ---------------------------------------------------------------

    private async Task TogglePipAsync()
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null) return;
        switch (await tab.TogglePictureInPictureAsync())
        {
            case PipResult.NoVideo:
                ShowToast(Icons.Info, "No video found", "Picture-in-picture works with videos on the page itself (not inside embedded frames).");
                break;
            case PipResult.Failed:
                ShowToast(Icons.Warning, "Picture-in-picture didn't start", "Start the video playing, then try again.");
                break;
        }
    }

    // ---- QR code --------------------------------------------------------------------------

    private byte[]? _qrPng;

    private void ShowQr()
    {
        var tab = ViewModel.ActiveTab;
        if (tab is null || tab.IsNewTabPage) return;
        var url = tab.IsReaderPage ? tab.ReaderOriginalUrl ?? tab.Url : tab.Url;
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            _qrPng = new PngByteQRCode(data).GetGraphic(10);
            QrImage.Source = Decode(_qrPng);
            QrUrl.Text = url;
            QrPopup.IsOpen = true;
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("QR code", ex);
            ShowToast(Icons.Warning, "Couldn't make a QR code", "This address may be too long.");
        }
    }

    private void OnSiteQr(object sender, RoutedEventArgs e)
    {
        SiteInfoPopup.IsOpen = false;
        Dispatcher.BeginInvoke(ShowQr, DispatcherPriority.Input);
    }

    private void OnQrCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(QrUrl.Text); } catch { }
        QrPopup.IsOpen = false;
    }

    private void OnQrSave(object sender, RoutedEventArgs e)
    {
        if (_qrPng is null) return;
        var dlg = new SaveFileDialog { FileName = "relay-qr.png", Filter = "PNG image|*.png", Title = "Save QR code" };
        if (dlg.ShowDialog(this) == true)
        {
            try { File.WriteAllBytes(dlg.FileName, _qrPng); }
            catch (Exception ex) { ShowToast(Icons.Warning, "Couldn't save the image", ex.Message); }
        }
        QrPopup.IsOpen = false;
    }

    // ---- Command palette entries ----------------------------------------------------------

    partial void AddExtraCommands(List<PaletteItem> list, BrowserTab? tab, bool web)
    {
        if (!web || tab is null) return;
        list.Add(new(tab.IsReaderPage ? "Leave reader view" : "Reader view", "Page", "", "F9", () => _ = ToggleReaderAsync()));
        list.Add(new("Screenshot of visible area", "Page", "", "", () => _ = TakeScreenshotAsync(false)));
        list.Add(new("Screenshot of full page", "Page", "", "", () => _ = TakeScreenshotAsync(true)));
        list.Add(new("Picture-in-picture", "Page", "", "", () => _ = TogglePipAsync()));
        list.Add(new("QR code for this page", "Page", "", "", ShowQr));
    }
}
