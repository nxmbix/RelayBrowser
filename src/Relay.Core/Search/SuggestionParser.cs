using System.Text.Json;

namespace Relay.Core.Search;

public static class SuggestionParser
{
    /// <summary>Parses a suggestion response; never throws on malformed input.</summary>
    public static IReadOnlyList<string> Parse(string json, SuggestFormat format, int max = 6)
    {
        var results = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (format)
            {
                case SuggestFormat.OpenSearch:
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 &&
                        root[1].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root[1].EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String) Add(results, item.GetString());
                    }
                    break;
                case SuggestFormat.DuckDuckGo:
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("phrase", out var p) && p.ValueKind == JsonValueKind.String)
                                Add(results, p.GetString());
                    }
                    break;
            }
        }
        catch (JsonException) { }
        return results.Take(max).ToList();
    }

    private static void Add(List<string> list, string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        s = s.Trim();
        if (s.Length > 200) return;
        if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
    }
}
