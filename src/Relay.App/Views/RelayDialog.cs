using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Relay.App.Controls;

namespace Relay.App.Views;

/// <summary>
/// A small themed modal dialog built in code: title, message, optional inputs, optional checkbox and up to three buttons.
/// Used for confirmations, permission prompts, renaming and bookmark editing.
/// It is a borderless transparent window holding one rounded card with a soft shadow, so it never shows a system frame
/// and always follows the app's light or dark theme.
/// </summary>
public sealed class RelayDialog : Window
{
    private const double ShadowRoom = 28; // transparent margin around the card so the shadow isn't clipped

    private readonly StackPanel _body = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly List<(string Key, TextBox Box)> _inputs = [];
    private ComboBox? _combo;
    private readonly List<(string Key, CheckBox Box)> _checks = [];
    private TextBlock? _error;
    private bool _hasDanger;
    private readonly Border _card;
    private readonly ScaleTransform _scale = new(0.96, 0.96);

    public string? Result { get; private set; }

    public RelayDialog(Window? owner, string title, string? message = null, string? glyph = null)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = title;
        if (owner is not null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.UI");

        var stack = new StackPanel();
        _card = new Border
        {
            Width = 400,
            Margin = new Thickness(ShadowRoom),
            Padding = new Thickness(24, 22, 24, 20),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = stack,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        var warning = glyph == Icons.Warning;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        if (glyph is not null)
        {
            var symbol = new TextBlock
            {
                Text = glyph, Style = (Style)FindResource("Text.Icon"), FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            symbol.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Brush.Danger" : "Brush.Accent");
            var icon = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top, Child = symbol,
            };
            icon.SetResourceReference(Border.BackgroundProperty, warning ? "Brush.DangerSubtle" : "Brush.AccentSubtle");
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
        }
        var heading = new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        header.Children.Add(heading);
        stack.Children.Add(header);

        if (!string.IsNullOrEmpty(message))
        {
            var msg = new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 13.5,
                Margin = new Thickness(glyph is null ? 0 : 50, 0, 0, 16),
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            stack.Children.Add(msg);
        }
        stack.Children.Add(_body);
        _buttons.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_buttons);
        Content = _card;

        // The card is the whole window, so let it be dragged by its background.
        _card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border or StackPanel or DockPanel) { try { DragMove(); } catch { } } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Result = null; DialogResult = false; }
        };
        Loaded += (_, _) => AnimateIn();
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    /// <summary>A card showing a file the way Windows does: its own icon, its name and a details line.</summary>
    public RelayDialog AddFile(string path, string? details = null)
    {
        var (icon, typeName) = FileIcons.Get(path);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 4, 0, 12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var row = new DockPanel();
        FrameworkElement picture = icon is not null
            ? new Image { Source = icon, Width = 40, Height = 40, Stretch = Stretch.Uniform }
            : new TextBlock { Text = Icons.Page, Style = (Style)FindResource("Text.Icon"), FontSize = 30 };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        picture.Margin = new Thickness(0, 0, 14, 0);
        picture.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = Path.GetFileName(path), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        text.Children.Add(name);
        var line = string.Join(" · ", new[] { typeName, details }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (line.Length > 0)
        {
            var sub = new TextBlock { Text = line, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            text.Children.Add(sub);
        }
        row.Children.Add(text);
        card.Child = row;
        _body.Children.Add(card);
        return this;
    }

    /// <summary>A red warning line with an icon, for risky actions.</summary>
    public RelayDialog AddWarning(string text)
    {
        var row = new DockPanel { Margin = new Thickness(2, 0, 0, 10) };
        var sign = new TextBlock { Text = Icons.Warning, Style = (Style)FindResource("Text.Icon"), FontSize = 14, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        sign.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        DockPanel.SetDock(sign, Dock.Left);
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        row.Children.Add(sign);
        row.Children.Add(t);
        _body.Children.Add(row);
        return this;
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
            Style = (Style)FindResource(primary ? "Button.Primary" : danger ? "Button.DangerFilled" : isCancel ? "Button.Neutral" : "Button.Secondary"),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 88,
            Height = 34,
            IsDefault = primary,
            IsCancel = isCancel,
        };
        if (danger) _hasDanger = true;
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
            // Enter confirms, except for destructive actions, where the safe choice (Cancel) starts focused.
            else if (_buttons.Children.Count > 0) _buttons.Children[_hasDanger ? 0 : _buttons.Children.Count - 1].Focus();
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

    /// <summary>
    /// Asks about a risky file: shows the file itself (icon, name, details), a red warning, a gray "No" and a red "Yes".
    /// </summary>
    public static bool ConfirmFile(Window? owner, string question, string path, string? details, string warning, string yes = "Yes", string no = "No") =>
        new RelayDialog(owner, question)
            .AddFile(path, details)
            .AddWarning(warning)
            .AddButton("no", no, isCancel: true)
            .AddButton("yes", yes, danger: true)
            .Ask() == "yes";

    public static void Info(Window? owner, string title, string message, string? glyph = null) =>
        new RelayDialog(owner, title, message, glyph).AddButton("ok", "OK", primary: true).Ask();
}
