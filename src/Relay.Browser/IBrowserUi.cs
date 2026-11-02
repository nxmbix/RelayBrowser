namespace Relay.Browser;

public enum PermissionPromptResult { Allow, Block, Dismiss }

public sealed record PermissionPrompt(string Origin, string KindName, string Description);

/// <summary>
/// Implemented by the WPF app so the engine layer can ask the user questions
/// without depending on any particular window.
/// </summary>
public interface IBrowserUi
{
    /// <summary>Asks whether a site may use a capability. <paramref name="remember"/> is set when the user wants it saved.</summary>
    Task<(PermissionPromptResult Result, bool Remember)> AskPermissionAsync(PermissionPrompt prompt, bool canRemember);

    /// <summary>Shows a Save As dialog. Returns null when cancelled.</summary>
    Task<string?> AskSavePathAsync(string suggestedPath);

    /// <summary>Warns about a file type that can run code. True to keep the download.</summary>
    Task<bool> ConfirmDangerousDownloadAsync(string fileName, string host);

    /// <summary>Lets the UI show a brief "download started/finished" notice.</summary>
    void NotifyDownload(DownloadItem item, bool finished);

    /// <summary>Opens RELAY settings (used by the new tab page).</summary>
    void OpenSettings(string? section);
}
