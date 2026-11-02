using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Relay.App.Controls;

namespace Relay.App.Views;

/// <summary>
/// A small themed modal dialog built in code: title, message, optional inputs, optional checkbox and up to three buttons.
/// Used for confirmations, permission prompts, renaming and bookmark editing.
/// </summary>
public sealed class RelayDialog : Window
{
    private readonly StackPanel _body = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly List<(string Key, TextBox Box)> _inputs = [];
    private ComboBox? _combo;
    private readonly List<(string Key, CheckBox Box)> _checks = [];
    private TextBlock? _error;

    public string? Result { get; private set; }

    public RelayDialog(Window? owner, string title, string? message = null, string? glyph = null)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = title;
        if (owner is not null && owner.IsVisible) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        var root = new Border { Padding = new Thickness(24, 22, 24, 20), Width = 420 };
        var stack = new StackPanel();
        root.Child = stack;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        if (glyph is not null)
        {
            var icon = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Text.Icon"), FontSize = 15 },
            };
            icon.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSubtle");
            ((TextBlock)icon.Child).SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
        }
        header.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.Heading"), FontSize = 16, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(header);

        if (!string.IsNullOrEmpty(message))
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), LineHeight = 20 };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            stack.Children.Add(msg);
        }
        stack.Children.Add(_body);
        _buttons.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_buttons);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Result = null; DialogResult = false; }
        };
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this, (bool)(Application.Current.TryFindResource("Theme.IsDark") ?? false));
    }

    public RelayDialog AddInput(string key, string label, string value = "", string? placeholder = null)
    {
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var box = new TextBox { Text = value, Style = (Style)FindResource("TextField"), Margin = new Thickness(0, 0, 0, 12) };
        if (placeholder is not null) box.ToolTip = placeholder;
        _body.Children.Add(l);
        _body.Children.Add(box);
        _inputs.Add((key, box));
        return this;
    }

    public RelayDialog AddChoice(string label, IEnumerable<(object Value, string Text)> items, object? selected)
    {
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _combo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            _combo.Items.Add(item);
            if (Equals(value, selected)) _combo.SelectedItem = item;
        }
        if (_combo.SelectedItem is null && _combo.Items.Count > 0) _combo.SelectedIndex = 0;
        _body.Children.Add(l);
        _body.Children.Add(_combo);
        return this;
    }

    public RelayDialog AddCheck(string text, bool isChecked = false, string key = "check")
    {
        var cb = new CheckBox { Content = text, IsChecked = isChecked, Style = (Style)FindResource("ToggleSwitch"), Margin = new Thickness(0, 4, 0, 8) };
        _body.Children.Add(cb);
        _checks.Add((key, cb));
        return this;
    }

    public RelayDialog AddNote(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        _body.Children.Add(t);
        return this;
    }

    public RelayDialog AddButton(string key, string text, bool primary = false, bool danger = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "Button.Primary" : danger ? "Button.Danger" : "Button.Secondary"),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 86,
            IsDefault = primary,
            IsCancel = isCancel,
        };
        b.Click += (_, _) =>
        {
            if (primary && Validate is not null && Validate(this) is { } error)
            {
                ShowError(error);
                return;
            }
            Result = key;
            DialogResult = !isCancel;
        };
        _buttons.Children.Add(b);
        return this;
    }

    /// <summary>Optional validation for the primary button; return an error message to keep the dialog open.</summary>
    public Func<RelayDialog, string?>? Validate { get; set; }

    private void ShowError(string text)
    {
        if (_error is null)
        {
            _error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
            _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            _body.Children.Add(_error);
        }
        _error.Text = text;
    }

    public string Input(string key) => _inputs.FirstOrDefault(i => i.Key == key).Box?.Text?.Trim() ?? string.Empty;
    public object? Choice => (_combo?.SelectedItem as ComboBoxItem)?.Tag;
    public bool Checked => _checks.Count > 0 && _checks[0].Box.IsChecked == true;
    public bool IsChecked(string key) => _checks.FirstOrDefault(c => c.Key == key).Box?.IsChecked == true;

    /// <summary>Shows the dialog modally and returns the key of the clicked button (null when dismissed).</summary>
    public string? Ask()
    {
        Loaded += (_, _) =>
        {
            if (_inputs.Count > 0) { _inputs[0].Box.Focus(); _inputs[0].Box.SelectAll(); }
            else if (_buttons.Children.Count > 0) _buttons.Children[_buttons.Children.Count - 1].Focus();
        };
        try { ShowDialog(); } catch (InvalidOperationException) { return null; }
        return Result;
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool danger = false, string? glyph = null)
    {
        var d = new RelayDialog(owner, title, message, glyph)
            .AddButton("cancel", "Cancel", isCancel: true);
        if (danger) d.AddButton("ok", confirmText, danger: true);
        else d.AddButton("ok", confirmText, primary: true);
        return d.Ask() == "ok";
    }

    public static void Info(Window? owner, string title, string message, string? glyph = null) =>
        new RelayDialog(owner, title, message, glyph).AddButton("ok", "OK", primary: true).Ask();
}
