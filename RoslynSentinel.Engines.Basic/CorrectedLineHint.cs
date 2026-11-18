using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Pure text logic behind the CS0117 / CS0103 "corrected line" hint: picks the existing name closest to a misspelled one
/// and rewrites the rejected source line with it. A model that misspells a name once sees the wrong line repeated in the
/// compiler error and tends to repeat the slip (ShippingKinds.Ownight, then Oovernight); showing the line with the right
/// spelling puts the correct text in front of it. No Roslyn types here, so the rules are unit-testable on strings.
/// </summary>
public static class CorrectedLineHint
{
    /// <summary>Hard cap on the edit distance, whatever the name length.</summary>
    private const int MaxDistanceCap = 3;

    /// <summary>
    /// Returns the name in <paramref name="names"/> closest to <paramref name="wanted"/>, or null when none is close.
    /// Rule (the tighter of three): edit distance at most <see cref="MaxDistanceCap"/>, at most half the length of the
    /// wanted name (so 'Zebra' needs distance 2, 'cnt' needs 1), and within <see cref="NameCandidates.Compare"/>'s strict
    /// rule (max(2, 40% of the longer name)). A case-insensitive exact match has distance 0 and always wins. Ties are
    /// broken by ordinal name order, so the result does not depend on enumeration order.
    /// </summary>
    public static string? FindClosest(string wanted, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return null;
        }

        var limit = LimitFor(wanted);
        var ordered = EligibleNames(wanted, names);

        var closest = NameCandidates.Rank(wanted, ordered, n => n, cap: 1, strict: true);
        return closest.Count > 0 && closest[0].Distance <= limit ? closest[0].Item : null;
    }

    /// <summary>
    /// Counts the distinct names in <paramref name="names"/> that <see cref="FindClosest"/> would accept as the closest name on its own, that is, the
    /// ones within the same distance limit and the same strict similarity rule. 1 means the correction is unambiguous; 2 or more means several names
    /// are in range and <see cref="FindClosest"/> only breaks the tie by distance and ordinal order. 0 for a blank <paramref name="wanted"/>.
    /// </summary>
    internal static int CountWithinLimit(string wanted, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return 0;
        }

        var limit = LimitFor(wanted);
        var eligible = EligibleNames(wanted, names);
        return NameCandidates.Rank(wanted, eligible, n => n, cap: eligible.Count, strict: true).Count(r => r.Distance <= limit);
    }

    private static int LimitFor(string wanted) => Math.Min(MaxDistanceCap, wanted.Length / 2);

    // Names that may be suggested: identifier-shaped, not the wanted name itself, distinct, in ordinal order so ties do not depend on enumeration order.
    private static List<string> EligibleNames(string wanted, IEnumerable<string> names)
        => names
            .Where(n => n.Length > 0 && (char.IsLetter(n[0]) || n[0] == '_') && !string.Equals(n, wanted, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns <paramref name="sourceLine"/> with the misspelled identifier replaced by <paramref name="closestName"/>, or
    /// null when the line is empty or the identifier is not found in it as a whole word. Only the one occurrence the
    /// diagnostic points at is replaced: with <paramref name="memberAccess"/> true (CS0117) only occurrences written
    /// after a '.' qualify, with false (CS0103) only unqualified ones. The quoted line is trimmed, so the diagnostic
    /// column cannot be mapped to it exactly; among several qualifying occurrences the first one at or before
    /// <paramref name="startColumn"/> is taken (the leftmost, which is the first error of a grouped diagnostic).
    /// </summary>
    public static string? RewriteLine(string? sourceLine, string wrongName, string closestName, int startColumn, bool memberAccess)
    {
        if (string.IsNullOrWhiteSpace(sourceLine) || string.IsNullOrEmpty(wrongName))
        {
            return null;
        }

        var occurrences = Regex.Matches(sourceLine, $@"(?<![\w@]){Regex.Escape(wrongName)}(?!\w)")
            .Where(m => FollowsDot(sourceLine, m.Index) == memberAccess)
            .ToList();
        if (occurrences.Count == 0)
        {
            return null;
        }

        var chosen = occurrences.FirstOrDefault(m => m.Index <= startColumn - 1) ?? occurrences[0];
        return string.Concat(sourceLine.AsSpan(0, chosen.Index), closestName, sourceLine.AsSpan(chosen.Index + chosen.Length));
    }

    private static bool FollowsDot(string line, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i]))
        {
            i--;
        }

        return i >= 0 && line[i] == '.' && (i == 0 || line[i - 1] != '.');
    }
}
