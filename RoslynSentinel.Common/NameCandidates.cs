namespace RoslynSentinel.Common;

/// <summary>
/// Name-similarity ranking for "did you mean" hints in not-found errors. Compare is the similarity rule (edit distance,
/// shared prefix, containment or subsequence); Rank orders a candidate list by it, with a noise guard and a cap. Syntax
/// only; callers collapse duplicate names themselves.
/// </summary>
public static class NameCandidates
{
    // A name is "similar" when it is a short edit away, shares a prefix of 4+ characters, or (for names of 4+ characters)
    // contains, is contained in, or is a subsequence of the other: CalcDisc is similar to CalculateDiscountedTotal.
    /// <summary>
    /// Reports whether <paramref name="actual"/> is a plausible intended name for <paramref name="wanted"/> (compared
    /// case-insensitively), together with the edit distance between the two names.
    /// </summary>
    public static (bool Similar, int Distance) Compare(string wanted, string actual)
    {
        var w = wanted.ToLowerInvariant();
        var a = actual.ToLowerInvariant();
        var distance = EditDistance(w, a);
        var prefix = 0;
        while (prefix < w.Length && prefix < a.Length && w[prefix] == a[prefix])
        {
            prefix++;
        }

        var similar = distance <= Math.Max(2, Math.Max(w.Length, a.Length) * 2 / 5)
                      || prefix >= 4
                      || (Math.Min(w.Length, a.Length) >= 4
                          && (w.Contains(a, StringComparison.Ordinal) || a.Contains(w, StringComparison.Ordinal) || IsSubsequence(w, a) || IsSubsequence(a, w)));
        return (similar, distance);
    }

    /// <summary>
    /// Returns the items whose name is similar to <paramref name="wanted"/>, nearest first (ties keep the original
    /// sequence order), at most <paramref name="cap"/>. Returns an empty list when <paramref name="wanted"/> is blank or
    /// shorter than 3 characters. Items whose name equals <paramref name="wanted"/> ignoring case come back with distance 0.
    /// </summary>
    public static IReadOnlyList<(T Item, int Distance)> Rank<T>(string wanted, IEnumerable<T> items, Func<T, string> nameSelector, int cap = 3)
    {
        if (string.IsNullOrWhiteSpace(wanted) || wanted.Length < 3)
        {
            return Array.Empty<(T Item, int Distance)>();
        }

        return items
            .Select(item => (Item: item, Cmp: Compare(wanted, nameSelector(item))))
            .Where(x => x.Cmp.Similar)
            .OrderBy(x => x.Cmp.Distance)
            .Take(cap)
            .Select(x => (x.Item, x.Cmp.Distance))
            .ToList();
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var c in haystack)
        {
            if (i < needle.Length && needle[i] == c)
            {
                i++;
            }
        }

        return i == needle.Length;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
