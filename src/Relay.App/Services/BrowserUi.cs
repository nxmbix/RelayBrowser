using System.Windows;
using Microsoft.Win32;
using Relay.App.Controls;
using Relay.App.Views;
using Relay.Browser;

namespace Relay.App.Services;

/// <summary>WPF implementation of the prompts the browser engine needs.</summary>
public sealed class BrowserUi : IBrowserUi
{
    private Window? Owner => App.Current.WindowManager?.LastActive;

    public async Task<(PermissionPromptResult Result, bool Remember)> AskPermissionAsync(PermissionPrompt prompt, bool canRemember)
    {
        await Task.Yield();
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var host = prompt.Origin.Replace("https://", "").Replace("http://", "");
            var d = new RelayDialog(Owner, $"{host} wants to", null, Icons.Info);
            d.AddNote(prompt.Description);
            if (canRemember) d.AddCheck("Remember this decision for this site", true);
            d.AddButton("block", "Block").AddButton("allow", "Allow", primary: true);
            var r = d.Ask();
            var result = r switch { "allow" => PermissionPromptResult.Allow, "block" => PermissionPromptResult.Block, _ => PermissionPromptResult.Dismiss };
            return (result, canRemember && d.Checked && result != PermissionPromptResult.Dismiss);
        });
    }

    public async Task<string?> AskSavePathAsync(string suggestedPath)
    {
        await Task.Yield();
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new SaveFileDialog
            {
                FileName = Path.GetFileName(suggestedPath),
                InitialDirectory = Path.GetDirectoryName(suggestedPath),
                OverwritePrompt = true,
                AddExtension = false,
                Title = "Save download",
            };
            var ext = Path.GetExtension(suggestedPath);
            dlg.Filter = string.IsNullOrEmpty(ext) ? "All files|*.*" : $"{ext.TrimStart('.').ToUpperInvariant()} file|*{ext}|All files|*.*";
            return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
        });
    }

    public async Task<bool> ConfirmDangerousDownloadAsync(string fileName, string host)
    {
        await Task.Yield();
        return await Application.Current.Dispatcher.InvokeAsync(() => RelayDialog.Confirm(Owner,
            "Keep this file?",
            $"“{fileName}” from {host} can run programs on your computer. Only keep it if you trust where it came from. RELAY will never open it automatically.",
            "Keep file", danger: true, glyph: Icons.Warning));
    }

    public void NotifyDownload(DownloadItem item, bool finished) =>
        App.Current.WindowManager?.LastActive?.ShowDownloadNotice(item, finished);

    public void OpenSettings(string? section) => App.Current.ShowSettings(section);
}
