using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Relay.App.Controls;

/// <summary>
/// Rounds the corners of a hosted native window (the WebView2 page). WPF clipping can't reach a child HWND,
/// so the corners are cut with a window region instead.
/// </summary>
public static class RoundedHost
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedHost), new PropertyMetadata(0.0, OnRadiusChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(RoundedHost), new PropertyMetadata(false));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl cc) return;
        if (!(bool)cc.GetValue(HookedProperty))
        {
            cc.SetValue(HookedProperty, true);
            cc.SizeChanged += (_, _) => Schedule(cc);
            cc.Loaded += (_, _) => Schedule(cc);
            DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
                .AddValueChanged(cc, (_, _) => Schedule(cc));
        }
        Schedule(cc);
    }

    private static void Schedule(ContentControl cc) =>
        cc.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Apply(cc));

    private static void Apply(ContentControl cc)
    {
        if (cc.Content is not HwndHost host) return;
        try
        {
            var hwnd = host.Handle;
            if (hwnd == IntPtr.Zero)
            {
                RoutedEventHandler? once = null;
                once = (_, _) => { host.Loaded -= once; Schedule(cc); };
                host.Loaded += once;
                return;
            }
            if (PresentationSource.FromVisual(host)?.CompositionTarget is not { } target) return;
            var m = target.TransformToDevice;
            var w = (int)Math.Ceiling(host.ActualWidth * m.M11);
            var h = (int)Math.Ceiling(host.ActualHeight * m.M22);
            var radius = GetRadius(cc);
            if (radius <= 0 || w <= 0 || h <= 0)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }
            var d = (int)Math.Round(radius * 2 * m.M11);
            var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn);
        }
        catch { /* the page is being torn down */ }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
}
