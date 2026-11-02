namespace Relay.App.ViewModels;

/// <summary>One entry in the Ctrl+K command palette: a command, an open tab, a bookmark or a history page.</summary>
public sealed record PaletteItem(string Title, string Detail, string Glyph, string Gesture, Action Run)
{
    public int Score { get; init; }
}
