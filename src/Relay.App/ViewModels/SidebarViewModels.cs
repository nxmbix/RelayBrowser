using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Relay.Browser;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.App.ViewModels;

public enum SidebarPanel { None, Bookmarks, History, Downloads, ReadingList }

/// <summary>A row with a lazily loaded favicon.</summary>
public abstract partial class FaviconRow : ObservableObject
{
    [ObservableProperty] private ImageSource? _favicon;

    public async void LoadFavicon(FaviconCache cache, string? url)
    {
        try { Favicon = await cache.GetForUrlAsync(url); } catch { }
    }
}

public sealed class HistoryRow : FaviconRow
{
    public required HistoryEntry Entry { get; init; }
    public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? UrlDisplay.Short(Entry.Url) : Entry.Title;
    public string Url => Entry.Url;
    public string Host => UrlDisplay.Host(Entry.Url);
    public string Time => Entry.VisitedAt.ToLocalTime().ToString("t");
    public required string Group { get; init; }
}

public sealed class BookmarkRow : FaviconRow
{
    public required Bookmark Bookmark { get; init; }
    public string Title => Bookmark.Title;
    public string? Url => Bookmark.Url;
    public bool IsFolder => Bookmark.IsFolder;
    public string Detail => IsFolder ? $"{ChildCount} item{(ChildCount == 1 ? "" : "s")}" : UrlDisplay.Short(Bookmark.Url);
    public int ChildCount { get; init; }
}

public sealed class ReadingRow : FaviconRow
{
    public required ReadingListItem Item { get; init; }
    public string Title => Item.Title;
    public string Url => Item.Url;
    public string Host => UrlDisplay.Host(Item.Url);
    public bool IsRead => Item.IsRead;
}

public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private readonly BrowserServices _s;
    private CancellationTokenSource? _cts;

    public HistoryPanelViewModel(BrowserServices s)
    {
        _s = s;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.Group)));
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _canLoadMore;

    private int _loaded;
    private const int Page = 150;

    partial void OnQueryChanged(string value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await Task.Delay(Query.Length > 0 ? 150 : 0, cts.Token);
            var entries = Query.Trim().Length > 0
                ? await _s.History.SearchAsync(Query.Trim(), 300)
                : await _s.History.GetRecentAsync(Page, 0);
            if (cts.IsCancellationRequested) return;
            Rows.Clear();
            _loaded = 0;
            Append(entries);
            CanLoadMore = Query.Trim().Length == 0 && entries.Count == Page;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _s.Paths.LogError("History panel", ex); }
    }

    public async Task LoadMoreAsync()
    {
        var entries = await _s.History.GetRecentAsync(Page, _loaded);
        Append(entries);
        CanLoadMore = entries.Count == Page;
    }

    private void Append(List<HistoryEntry> entries)
    {
        var now = DateTime.Now;
        foreach (var e in entries)
        {
            var row = new HistoryRow { Entry = e, Group = DateGrouping.Label(e.VisitedAt.ToLocalTime(), now) };
            Rows.Add(row);
            row.LoadFavicon(_s.FaviconCache, e.Url);
        }
        _loaded += entries.Count;
        IsEmpty = Rows.Count == 0;
    }

    public async Task DeleteAsync(HistoryRow row)
    {
        await _s.History.DeleteAsync(row.Entry.Id);
        Rows.Remove(row);
        IsEmpty = Rows.Count == 0;
    }
}

public sealed partial class BookmarksPanelViewModel : ObservableObject
{
    private readonly BrowserServices _s;
    private List<Bookmark> _all = [];

    public BookmarksPanelViewModel(BrowserServices s)
    {
        _s = s;
        _s.Bookmarks.Changed += async (_, _) => { if (IsActive) await LoadAsync(); };
    }

    public bool IsActive { get; set; }
    public ObservableCollection<BookmarkRow> Rows { get; } = [];
    public ObservableCollection<Bookmark> Path { get; } = [];

    [ObservableProperty] private Bookmark? _folder;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _folderTitle = "Bookmarks";
    [ObservableProperty] private bool _canGoUp;

    partial void OnQueryChanged(string value) => Refresh();

    public IReadOnlyList<Bookmark> AllFolders => _all.Where(b => b.IsFolder).ToList();

    public async Task LoadAsync()
    {
        try
        {
            _all = await _s.Bookmarks.GetAllAsync();
            if (Folder is not null) Folder = _all.FirstOrDefault(b => b.Id == Folder.Id);
            Refresh();
        }
        catch (Exception ex) { _s.Paths.LogError("Bookmarks panel", ex); }
    }

    public void Open(Bookmark? folder)
    {
        Folder = folder;
        Query = string.Empty;
        Refresh();
    }

    public void Up() => Open(Folder?.ParentId is long p ? _all.FirstOrDefault(b => b.Id == p) : null);

    private void Refresh()
    {
        Rows.Clear();
        IEnumerable<Bookmark> items;
        var q = Query.Trim();
        if (q.Length > 0)
        {
            items = _all.Where(b => !b.IsFolder &&
                (b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || (b.Url?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
                .OrderBy(b => b.Title).Take(200);
        }
        else
        {
            var parentId = Folder?.Id;
            items = _all.Where(b => b.ParentId == parentId).OrderBy(b => b.Position).ThenBy(b => b.Id);
        }
        foreach (var b in items)
        {
            var row = new BookmarkRow { Bookmark = b, ChildCount = b.IsFolder ? _all.Count(x => x.ParentId == b.Id) : 0 };
            Rows.Add(row);
            if (!b.IsFolder) row.LoadFavicon(_s.FaviconCache, b.Url);
        }
        IsEmpty = Rows.Count == 0;
        FolderTitle = q.Length > 0 ? "Search results" : Folder?.Title ?? "Bookmarks";
        CanGoUp = Folder is not null && q.Length == 0;
    }

    public string FolderPath(long? id)
    {
        var parts = new List<string>();
        var guard = 0;
        while (id is long i && guard++ < 50)
        {
            var f = _all.FirstOrDefault(b => b.Id == i);
            if (f is null) break;
            parts.Insert(0, f.Title);
            id = f.ParentId;
        }
        return parts.Count == 0 ? "Bookmarks" : "Bookmarks › " + string.Join(" › ", parts);
    }
}

public sealed partial class ReadingListPanelViewModel : ObservableObject
{
    private readonly BrowserServices _s;

    public ReadingListPanelViewModel(BrowserServices s)
    {
        _s = s;
        _s.ReadingList.Changed += async (_, _) => { if (IsActive) await LoadAsync(); };
    }

    public bool IsActive { get; set; }
    public ObservableCollection<ReadingRow> Rows { get; } = [];
    [ObservableProperty] private bool _isEmpty;

    public async Task LoadAsync()
    {
        try
        {
            var items = await _s.ReadingList.GetAllAsync();
            Rows.Clear();
            foreach (var i in items)
            {
                var row = new ReadingRow { Item = i };
                Rows.Add(row);
                row.LoadFavicon(_s.FaviconCache, i.Url);
            }
            IsEmpty = Rows.Count == 0;
        }
        catch (Exception ex) { _s.Paths.LogError("Reading list", ex); }
    }
}

public sealed partial class SidebarViewModel : ObservableObject
{
    public SidebarViewModel(BrowserServices s)
    {
        Services = s;
        History = new HistoryPanelViewModel(s);
        Bookmarks = new BookmarksPanelViewModel(s);
        ReadingList = new ReadingListPanelViewModel(s);
    }

    public BrowserServices Services { get; }
    public HistoryPanelViewModel History { get; }
    public BookmarksPanelViewModel Bookmarks { get; }
    public ReadingListPanelViewModel ReadingList { get; }
    public DownloadManager Downloads => Services.Downloads;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOpen), nameof(PanelTitle))]
    private SidebarPanel _panel;

    public bool IsOpen => Panel != SidebarPanel.None;

    public string PanelTitle => Panel switch
    {
        SidebarPanel.Bookmarks => "Bookmarks",
        SidebarPanel.History => "History",
        SidebarPanel.Downloads => "Downloads",
        SidebarPanel.ReadingList => "Reading list",
        _ => string.Empty,
    };

    async partial void OnPanelChanged(SidebarPanel value)
    {
        Bookmarks.IsActive = value == SidebarPanel.Bookmarks;
        ReadingList.IsActive = value == SidebarPanel.ReadingList;
        switch (value)
        {
            case SidebarPanel.History: await History.LoadAsync(); break;
            case SidebarPanel.Bookmarks: await Bookmarks.LoadAsync(); break;
            case SidebarPanel.ReadingList: await ReadingList.LoadAsync(); break;
            case SidebarPanel.Downloads: await Downloads.LoadAsync(); break;
        }
    }

    /// <summary>Opens a panel, or closes it if it is already showing.</summary>
    public void Toggle(SidebarPanel p) => Panel = Panel == p ? SidebarPanel.None : p;
}
