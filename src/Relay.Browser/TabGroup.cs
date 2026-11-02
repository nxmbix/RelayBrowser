using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Relay.Browser;

/// <summary>A named, coloured set of adjacent tabs that can be collapsed.</summary>
public sealed partial class TabGroup : ObservableObject
{
    public static IReadOnlyList<(string Name, Color Color)> Palette { get; } =
    [
        ("Grey", Color.FromRgb(0x7C, 0x83, 0x8A)),
        ("Blue", Color.FromRgb(0x4A, 0x7B, 0xD0)),
        ("Teal", Color.FromRgb(0x2E, 0x9A, 0x8A)),
        ("Green", Color.FromRgb(0x4E, 0x9A, 0x50)),
        ("Yellow", Color.FromRgb(0xC9, 0x9A, 0x2E)),
        ("Orange", Color.FromRgb(0xD0, 0x74, 0x3C)),
        ("Red", Color.FromRgb(0xC9, 0x4B, 0x4B)),
        ("Pink", Color.FromRgb(0xC4, 0x5A, 0x92)),
        ("Purple", Color.FromRgb(0x85, 0x66, 0xC9)),
    ];

    public TabGroup(string name, string colorName)
    {
        _name = name;
        _colorName = Palette.Any(p => p.Name == colorName) ? colorName : "Blue";
    }

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty] private string _name;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Brush), nameof(SubtleBrush))] private string _colorName;
    [ObservableProperty] private bool _isCollapsed;
    /// <summary>Number of tabs in the group (shown on the header while collapsed).</summary>
    [ObservableProperty] private int _count;

    public SolidColorBrush Brush
    {
        get
        {
            var b = new SolidColorBrush(Palette.First(p => p.Name == ColorName).Color);
            b.Freeze();
            return b;
        }
    }

    public SolidColorBrush SubtleBrush
    {
        get
        {
            var c = Palette.First(p => p.Name == ColorName).Color;
            var b = new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? string.Empty : Name;
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
