using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Relay.App.Controls;

public static class MainWindowConverters
{
    /// <summary>Visible when not loading and no favicon is available.</summary>
    public static readonly IMultiValueConverter PlaceholderIcon = new MultiConverter(v =>
        v.Length == 2 && v[0] is false && v[1] is null ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Visible when any bound value is true.</summary>
    public static readonly IMultiValueConverter AnyTrue = new MultiConverter(v =>
        v.Any(x => x is true) ? Visibility.Visible : Visibility.Collapsed);

    public static readonly IValueConverter ZeroToVisible = new Converter(v => v is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>The blocker shield shows for web pages only.</summary>
    public static readonly IMultiValueConverter ShieldVisible = new MultiConverter(v =>
        v.Length == 2 && v[0] is false && v[1] is Relay.Browser.SecurityState st &&
        st is Relay.Browser.SecurityState.Secure or Relay.Browser.SecurityState.NotSecure ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>"7", "42", "99+".</summary>
    public static readonly IValueConverter CompactCount = new Converter(v => v is int i ? (i > 99 ? "99+" : i.ToString()) : string.Empty);

    /// <summary>Reader button: on web pages, and while the reader view is open.</summary>
    public static readonly IMultiValueConverter ReaderVisible = new MultiConverter(v =>
        v.Length == 2 && (v[1] is true || v[0] is Relay.Browser.SecurityState st && st is Relay.Browser.SecurityState.Secure or Relay.Browser.SecurityState.NotSecure)
            ? Visibility.Visible : Visibility.Collapsed);

    public static readonly IValueConverter IsNonZero = new Converter(v => v is int i && i != 0);

    public static readonly IValueConverter IsNotNull = new Converter(v => v is not null);

    private sealed class MultiConverter(Func<object[], object> f) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => f(values);
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
    }

    private sealed class Converter(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
