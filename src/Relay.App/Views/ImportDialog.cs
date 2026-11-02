using System.Windows;
using Microsoft.Win32;
using Relay.App.Controls;
using Relay.Browser;
using Relay.Core.Bookmarks;
using Relay.Core.Import;
using Relay.Data;

namespace Relay.App.Views;

/// <summary>Import from another browser: one click for any Chromium browser or Firefox on this PC, or a bookmarks file.</summary>
public static class ImportDialog
{
    public static async Task ShowAsync(Window owner, BrowserServices s, bool firstRun = false)
    {
        var sources = await Task.Run(() => BrowserImport.Detect());
        var d = new RelayDialog(owner, firstRun ? "Bring your stuff to RELAY" : "Import browser data",
            sources.Count > 0
                ? "Copies bookmarks and history from another browser on this PC. It takes a few seconds and doesn't change anything in that browser."
                : "No other browsers were found on this PC. You can still import a bookmarks file exported from any browser.",
            Icons.Import);
        if (sources.Count > 0)
        {
            d.AddChoice("Import from", sources.Select((x, i) => ((object)i, x.DisplayName)), 0)
             .AddCheck("Bookmarks", true, "bookmarks")
             .AddCheck("History", true, "history")
             .AddNote("Passwords and cookies stay in the other browser: the engine RELAY uses has no supported way to add saved passwords.");
        }
        d.AddButton("file", "Bookmarks file…")
         .AddButton("cancel", firstRun ? "Not now" : "Cancel", isCancel: true);
        if (sources.Count > 0) d.AddButton("import", "Import", primary: true);

        var result = d.Ask();
        if (result == "file") { await ImportBookmarksFileAsync(owner, s); return; }
        if (result != "import" || d.Choice is not int index || index < 0 || index >= sources.Count) return;

        var source = sources[index];
        var wantBookmarks = d.IsChecked("bookmarks");
        var wantHistory = d.IsChecked("history");
        if (!wantBookmarks && !wantHistory) return;
        try
        {
            var (tree, visits) = await Task.Run(() => (
                wantBookmarks ? BrowserImport.ReadBookmarks(source) : null,
                wantHistory ? BrowserImport.ReadHistory(source) : new List<ImportedVisit>()));
            var bookmarks = tree is not null && tree.CountBookmarks() > 0
                ? await s.Bookmarks.ImportAsync(tree, $"Imported from {source.Browser}")
                : 0;
            var pages = visits.Count > 0 ? await s.History.ImportAsync(visits) : 0;
            RelayDialog.Info(owner, "Import finished",
                $"Copied {bookmarks} bookmarks and {pages} history entries from {source.DisplayName}.", Icons.Check);
        }
        catch (Exception ex)
        {
            s.Paths.LogError("Import from " + source.Browser, ex);
            RelayDialog.Info(owner, "Import failed",
                $"RELAY couldn't read {source.DisplayName}'s data. Close that browser and try again.\n\n{ex.Message}", Icons.Warning);
        }
    }

    /// <summary>Imports a bookmarks HTML file (the format every browser exports). Returns true when bookmarks were added.</summary>
    public static async Task<bool> ImportBookmarksFileAsync(Window owner, BrowserServices s)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import bookmarks",
            Filter = "Bookmark files (*.html;*.htm)|*.html;*.htm|All files|*.*",
        };
        if (dlg.ShowDialog(owner) != true) return false;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 50 * 1024 * 1024) throw new InvalidDataException("The file is too large to be a bookmark export.");
            var html = await File.ReadAllTextAsync(dlg.FileName);
            var tree = await Task.Run(() => NetscapeBookmarks.Parse(html));
            if (tree.CountBookmarks() == 0)
            {
                RelayDialog.Info(owner, "No bookmarks found", "That file doesn't contain bookmarks in the standard HTML format that browsers export.", Icons.Warning);
                return false;
            }
            var added = await s.Bookmarks.ImportAsync(tree, $"Imported {DateTime.Now:yyyy-MM-dd}");
            RelayDialog.Info(owner, "Bookmarks imported", $"{added} bookmarks were added to a new folder.", Icons.Check);
            return true;
        }
        catch (Exception ex)
        {
            s.Paths.LogError("Import bookmarks", ex);
            RelayDialog.Info(owner, "Import failed", ex.Message, Icons.Warning);
            return false;
        }
    }
}
