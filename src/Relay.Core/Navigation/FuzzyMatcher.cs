namespace Relay.Core.Navigation;

/// <summary>Small fuzzy matcher for the command palette: "nwpr" finds "New private window".</summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Scores how well <paramref name="query"/> matches <paramref name="text"/>. Returns -1 when the query's
    /// characters don't all appear in order. Higher is better: contiguous runs, word starts and prefixes score more.
    /// </summary>
    public static int Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        if (string.IsNullOrEmpty(text)) return -1;

        // Plain substring matches always beat scattered ones.
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var s = 1000 - idx * 4 - (text.Length - query.Length);
            if (idx == 0) s += 400;
            else if (!char.IsLetterOrDigit(text[idx - 1])) s += 200;
            return Math.Max(s, 300);
        }

        var score = 0;
        var ti = 0;
        var run = 0;
        foreach (var qc in query)
        {
            if (char.IsWhiteSpace(qc)) { run = 0; continue; }
            var found = false;
            while (ti < text.Length)
            {
                var tc = text[ti];
                if (char.ToLowerInvariant(tc) == char.ToLowerInvariant(qc))
                {
                    var wordStart = ti == 0 || !char.IsLetterOrDigit(text[ti - 1]) || (char.IsUpper(tc) && char.IsLower(text[ti - 1]));
                    score += 10 + (wordStart ? 25 : 0) + run * 8;
                    run++;
                    ti++;
                    found = true;
                    break;
                }
                run = 0;
                ti++;
            }
            if (!found) return -1;
        }
        return Math.Max(1, score - text.Length / 4);
    }
}
