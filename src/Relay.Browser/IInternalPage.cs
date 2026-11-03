namespace Relay.Browser;

/// <summary>
/// The browser side of one of RELAY's own pages (relay://settings). The page receives its data as JSON
/// and asks for changes through messages; only the trusted page, as the top-level document, is heard.
/// </summary>
public interface IInternalPage
{
    /// <summary>Tabs showing the page should refresh their data.</summary>
    event EventHandler? StateChanged;

    Task<string> BuildStateJsonAsync(BrowserTab tab);

    /// <summary>Handles a message from the page. Unknown or malformed messages are ignored.</summary>
    Task HandleMessageAsync(BrowserTab tab, string json);
}
