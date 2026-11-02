using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Relay.App.Controls;
using Relay.Browser;
using Relay.Core.Navigation;
using Relay.Core.Search;

namespace Relay.App.ViewModels;

public enum SuggestionKind { Navigate, Search, History, Bookmark, SearchSuggestion }

public sealed record Suggestion(SuggestionKind Kind, string Primary, string Secondary, string Target, string Glyph);

/// <summary>Address bar suggestions: what you typed, history, bookmarks and (optionally) search-engine suggestions.</summary>
public sealed partial class OmniboxViewModel : ObservableObject
{
    private static readonly HttpClient Http = CreateClient();
    private readonly BrowserServices _s;
    private readonly bool _isPrivate;
    private CancellationTokenSource? _cts;

    public OmniboxViewModel(BrowserServices services, bool isPrivate)
    {
        _s = services;
        _isPrivate = isPrivate;
    }

    public ObservableCollection<Suggestion> Suggestions { get; } = [];

    [ObservableProperty] private Suggestion? _selected;
    [ObservableProperty] private bool _isOpen;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(2.5),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RELAY/1.1");
        return c;
    }

    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
        Suggestions.Clear();
        Selected = null;
    }

    public async Task UpdateAsync(string text)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        text = text.Trim();
        if (text.Length == 0) { Close(); return; }

        var engine = _s.SearchEngine;
        var list = new List<Suggestion>();
        var typed = AddressParser.Parse(text, engine);
        if (typed is not null)
        {
            list.Add(typed.IsSearch
                ? new Suggestion(SuggestionKind.Search, text, $"Search with {engine.Name}", typed.Uri.AbsoluteUri, Icons.Search)
                : new Suggestion(SuggestionKind.Navigate, typed.Uri.AbsoluteUri, "Go to address", typed.Uri.AbsoluteUri, Icons.Globe));
        }
        Replace(list);

        try
        {
            await Task.Delay(90, token); // debounce while typing
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { list[0].Target };

            foreach (var b in await _s.Bookmarks.SearchAsync(text, 3))
            {
                if (b.Url is null || !seen.Add(b.Url)) continue;
                list.Add(new Suggestion(SuggestionKind.Bookmark, b.Title, UrlDisplay.Short(b.Url), b.Url, Icons.Star));
            }
            foreach (var h in await _s.History.SuggestAsync(text, 4))
            {
                if (!seen.Add(h.Url)) continue;
                var title = string.IsNullOrWhiteSpace(h.Title) ? UrlDisplay.Short(h.Url) : h.Title;
                list.Add(new Suggestion(SuggestionKind.History, title, UrlDisplay.Short(h.Url), h.Url, Icons.History));
            }
            token.ThrowIfCancellationRequested();
            Replace(list);

            var allowRemote = _s.Settings.Current.SearchSuggestions && (!_isPrivate || _s.Settings.Current.PrivateSearchSuggestions);
            if (allowRemote && engine.BuildSuggestUri(text) is { } suggestUri && text.Length <= 120)
            {
                using var resp = await Http.GetAsync(suggestUri, token);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(token);
                    var remote = SuggestionParser.Parse(body, engine.SuggestFormat, 5);
                    token.ThrowIfCancellationRequested();
                    foreach (var r in remote)
                    {
                        if (r.Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
                        var target = engine.BuildSearchUri(r).AbsoluteUri;
                        if (!seen.Add(target)) continue;
                        list.Add(new Suggestion(SuggestionKind.SearchSuggestion, r, string.Empty, target, Icons.Search));
                    }
                    Replace(list);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { /* offline: local suggestions are still shown */ }
        catch (Exception ex) { _s.Paths.LogError("Suggestions", ex); }
    }

    private void Replace(List<Suggestion> items)
    {
        var keep = Selected;
        Suggestions.Clear();
        foreach (var i in items.Take(10)) Suggestions.Add(i);
        Selected = keep is not null && Suggestions.Contains(keep) ? keep : null;
        IsOpen = Suggestions.Count > 0;
    }

    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0) return;
        var i = Selected is null ? (delta > 0 ? -1 : Suggestions.Count) : Suggestions.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Suggestions.Count - 1);
        Selected = Suggestions[i];
    }
}
