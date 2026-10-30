using RemoteManager.Core.History;

namespace RemoteManager.Core.Search;

/// <summary>A text to search in and how much a hit there counts (name more than description).</summary>
public readonly record struct SearchField(string Text, int Weight);

/// <summary>
/// Fuzzy matching for the quick launcher: every word of the query must be found in one of the fields,
/// as a substring or as a subsequence ("dc1" finds "dc01"). Better hits score higher: whole text,
/// prefix, word start, consecutive letters. Usage (history) breaks ties and lifts frequent targets.
/// </summary>
public static class FuzzySearch
{
    /// <summary>Score of <paramref name="query"/> in <paramref name="text"/>, or null when it does not match.</summary>
    public static int? Score(string query, string text)
    {
        if (query.Length == 0)
            return 0;
        if (text.Length == 0)
            return null;

        int index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index >= 0)
        {
            if (query.Length == text.Length)
                return 1000;
            int score = 600 - Math.Min(index, 100);
            if (index == 0)
                score += 200;
            else if (IsWordStart(text, index))
                score += 100;
            return score;
        }

        // Subsequence: each query letter in order; reward runs and word starts, penalize gaps.
        int total = 0, run = 0, position = 0, last = -1;
        foreach (char q in query)
        {
            int found = -1;
            for (int i = position; i < text.Length; i++)
            {
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(q))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
                return null;
            run = found == last + 1 ? run + 1 : 0;
            total += 10 + run * 15 + (IsWordStart(text, found) ? 20 : 0) - Math.Min(found - last - 1, 10);
            last = found;
            position = found + 1;
        }

        return Math.Clamp(total, 1, 400);
    }

    /// <summary>
    /// The matching items, best first. An empty query returns everything ordered by usage. Ties keep
    /// the input order, so pass the items sorted by name.
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(string query, IEnumerable<T> items, Func<T, IEnumerable<SearchField>> fields, Func<T, double>? usage = null)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(T Item, double Score, int Index)>();
        int index = 0;
        foreach (T item in items)
        {
            SearchField[] itemFields = [.. fields(item)];
            double total = 0;
            bool matches = true;
            foreach (string word in words)
            {
                string term = word.TrimStart('#');
                int? best = null;
                foreach (SearchField field in itemFields)
                {
                    if (Score(term, field.Text) is { } s && (best is null || s * field.Weight > best))
                        best = s * field.Weight;
                }

                if (best is null)
                {
                    matches = false;
                    break;
                }

                total += best.Value;
            }

            if (matches)
                scored.Add((item, total + (usage?.Invoke(item) ?? 0), index));
            index++;
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => s.Item)];
    }

    /// <summary>Bonus for frequently and recently used connections; small next to a better text match.</summary>
    public static double UsageBonus(UsageStats? stats, DateTime now)
    {
        if (stats is null)
            return 0;
        TimeSpan age = now - stats.LastUsed;
        double recency = age < TimeSpan.FromDays(1) ? 60 : age < TimeSpan.FromDays(7) ? 40 : age < TimeSpan.FromDays(30) ? 20 : 0;
        return recency + Math.Min(stats.Count, 20) * 6;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]) || (char.IsUpper(text[index]) && char.IsLower(text[index - 1]));
}
