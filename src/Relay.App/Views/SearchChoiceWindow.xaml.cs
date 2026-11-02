using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Relay.App.Controls;
using Relay.Core.Search;

namespace Relay.App.Views;

/// <summary>
/// First-run search engine choice. The list is shuffled and nothing is preselected,
/// so no engine gets an advantage from its position.
/// </summary>
public partial class SearchChoiceWindow : Window
{
    public sealed record Choice(string Id, string Name, string Description, string Initial, Brush Tint);

    public SearchChoiceWindow()
    {
        InitializeComponent();
        var rng = Random.Shared;
        Engines.ItemsSource = SearchEngines.BuiltIn
            .Select(e => new Choice(e.Id, e.Name, SearchEngines.Describe(e.Id), e.Name[..1], Tint(e.Id)))
            .OrderBy(_ => rng.Next())
            .ToList();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this, App.Current.Theme.IsDark);
    }

    /// <summary>The chosen engine id, or null if the window was closed without choosing.</summary>
    public string? SelectedId { get; private set; }

    private static Brush Tint(string id)
    {
        // Muted, neutral tiles — RELAY doesn't ship other companies' logos.
        var hue = id.Aggregate(7, (h, c) => h * 31 + c) % 360;
        var c = FromHsl(Math.Abs(hue), 0.30, 0.46);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s, X = C * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - C / 2;
        var (r, g, b) = h switch
        {
            < 60 => (C, X, 0.0), < 120 => (X, C, 0.0), < 180 => (0.0, C, X),
            < 240 => (0.0, X, C), < 300 => (X, 0.0, C), _ => (C, 0.0, X),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = Engines.SelectedItem as Choice;
        ContinueButton.IsEnabled = c is not null;
        Hint.Text = c is null ? "Pick one to continue." : $"{c.Name} will be your search engine.";
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (Engines.SelectedItem is not Choice c) return;
        SelectedId = c.Id;
        DialogResult = true;
    }
}
