using System.Windows;
using System.Windows.Controls;
using Relay.Browser;

namespace Relay.App.Controls;

/// <summary>
/// Lays the horizontal tab strip out in a row. Group headers take the width they need; tabs share
/// the rest evenly between <see cref="MinTabWidth"/> and <see cref="MaxTabWidth"/>, like a desktop browser.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly DependencyProperty MaxTabWidthProperty = DependencyProperty.Register(
        nameof(MaxTabWidth), typeof(double), typeof(TabStripPanel),
        new FrameworkPropertyMetadata(236.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinTabWidthProperty = DependencyProperty.Register(
        nameof(MinTabWidth), typeof(double), typeof(TabStripPanel),
        new FrameworkPropertyMetadata(44.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxTabWidth { get => (double)GetValue(MaxTabWidthProperty); set => SetValue(MaxTabWidthProperty, value); }
    public double MinTabWidth { get => (double)GetValue(MinTabWidthProperty); set => SetValue(MinTabWidthProperty, value); }

    /// <summary>Width each tab received in the last layout pass.</summary>
    public double TabWidth { get; private set; }

    private static bool IsHeader(UIElement child) => (child as FrameworkElement)?.DataContext is TabGroup;

    protected override Size MeasureOverride(Size available)
    {
        double headers = 0, height = 0;
        var tabs = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (IsHeader(child))
            {
                child.Measure(new Size(double.PositiveInfinity, available.Height));
                headers += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            else tabs++;
        }
        var avail = double.IsInfinity(available.Width) ? MaxTabWidth * Math.Max(tabs, 1) + headers : available.Width;
        TabWidth = tabs == 0 ? 0 : Math.Clamp(Math.Floor((avail - headers) / tabs), MinTabWidth, MaxTabWidth);
        foreach (UIElement child in InternalChildren)
        {
            if (IsHeader(child)) continue;
            child.Measure(new Size(TabWidth, available.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(Math.Min(TabWidth * tabs + headers, avail), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            var w = IsHeader(child) ? child.DesiredSize.Width : TabWidth;
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
