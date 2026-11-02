using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Relay.Browser;

namespace Relay.App.Views;

/// <summary>
/// Shown when the Evergreen WebView2 Runtime isn't installed. Built without theme resources because
/// it appears before the rest of the app is initialised. RELAY doesn't try to install anything itself.
/// </summary>
public sealed class RuntimeMissingWindow : Window
{
    public RuntimeMissingWindow()
    {
        Title = "RELAY";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13.5;

        var text = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x23));
        var muted = new SolidColorBrush(Color.FromRgb(0x55, 0x5E, 0x66));
        var accent = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x6D));

        var stack = new StackPanel { Margin = new Thickness(28, 26, 28, 24) };
        stack.Children.Add(new TextBlock { Text = "RELAY needs Microsoft Edge WebView2", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = text });
        stack.Children.Add(new TextBlock
        {
            Text = "RELAY displays web pages with the WebView2 Runtime, which is normally included with Windows 10 and Windows 11. " +
                   "It wasn't found on this computer.\n\nInstall the “Evergreen Runtime” from Microsoft's website (or ask your administrator), then start RELAY again. " +
                   "On many computers it can be installed for your user account without administrator rights.",
            TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 12, 0, 20), LineHeight = 20,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = "Open download page", Padding = new Thickness(14, 6, 14, 6), Background = accent, Foreground = Brushes.White, BorderBrush = accent };
        open.Click += (_, _) => App.OpenExternal(WebViewRuntime.DownloadPage);
        var close = new Button { Content = "Close", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        stack.Children.Add(buttons);
        Content = stack;
    }
}
