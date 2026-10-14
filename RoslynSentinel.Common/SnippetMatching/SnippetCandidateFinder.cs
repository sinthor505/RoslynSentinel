using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// The outcome of a closest-window search: the listed windows (ranked, capped), how many windows qualified in total,
/// and how many windows were scored. Data only.
/// </summary>
/// <param name="Windows">At most <see cref="SnippetCandidateFinder.MaxClosestWindows"/> windows, best first.</param>
/// <param name="TotalQualifying">Windows scoring at or above <see cref="SnippetCandidateFinder.MinClosestWindowScore"/>,
/// before the cap; never less than <c>Windows.Count</c>.</param>
/// <param name="WindowsExamined">How many candidate windows were scored (one per file line).</param>
internal sealed record ClosestWindowSearch(
    IReadOnlyList<SnippetClosestWindow> Windows, int TotalQualifying, int WindowsExamined);

/// <summary>
/// Builds the evidence a failed snippet lookup carries: the structured differences of a near-match and the closest
/// windows when nothing matched at any tier. Data only; no wording. Everything here is deterministic.
/// <para><b>Differences.</b> A near-match's file lines are compared with the snippet's content lines (fully blank leading
/// and trailing snippet lines dropped, as the matchers do), one to one. A line the exact matcher would accept (a first
/// line that starts mid-line, a last line that stops mid-line, every other line equal) has no entry. Otherwise the entry
/// kinds are LeadingWhitespace and TrailingWhitespace (the edge whitespace differs; a snippet's first line has no
/// leading difference and its last line no trailing one when it carries no such whitespace, because those edges are
/// fragments), InteriorWhitespace (the trimmed text differs only in whitespace runs, or in a way no other kind
/// explains), and LineEnding (the break after the line). There is no Case kind: a near-match is only ever probed
/// case-sensitively, because CaseInsensitive is accepted only with the loosest tier, which leaves no looser tier to
/// probe.</para>
/// <para><b>Closest windows.</b> Every file line starts a window of the snippet's content-line count (shortened at the end of
/// the file). Both sides are trimmed, whitespace-collapsed and, under CaseInsensitive, upper-cased invariantly (the
/// same equality as OrdinalIgnoreCase). A window is scored with <see cref="LineBlockAligner"/>, wrapped, not changed:
/// the aligner reports how many leading lines are equal; the line where it diverges earns partial credit from a
/// character-bigram similarity (capped below a full match), and the aligner then resumes on the lines after it, so a
/// window with one wrong line in the middle still scores high. Each line is weighted by its normalized length (at
/// least 1), so a stray <c>}</c> or blank line cannot qualify a window on its own. The score is earned weight over total
/// weight, 0..1. Windows below <see cref="MinClosestWindowScore"/> are dropped, the rest are ranked by score
/// descending then start line ascending, and only then truncated to <see cref="MaxClosestWindows"/>. Known limits: the
/// scoring pairs lines by position (an inserted or deleted line shifts the lines after it and costs their credit), and
/// listed windows can overlap.</para>
/// </summary>
internal static class SnippetCandidateFinder
{
    /// <summary>Most closest windows listed in a result; the full qualifying count is reported separately.</summary>
    internal const int MaxClosestWindows = 5;

    /// <summary>
    /// A window must score at least this to be listed: half the snippet's weight must be matched or near-matched.
    /// Conservative on purpose: a listed window that is not really close costs the reader more than an omitted one,
    /// and "nothing close" is a reportable result. A window that matches exactly one of two equal-weight lines scores
    /// exactly this and qualifies; one line of three does not.
    /// </summary>
    internal const double MinClosestWindowScore = 0.5;

    // A divergent line never earns a full line's credit, so a score of 1 always means a full match.
    private const double MaxPartialCredit = 0.99;

    private readonly record struct SnippetLinePart(string Text, string Ending);

    /// <summary>
    /// Compares the snippet with the file lines a near-match covers and lists the differing lines. See the type
    /// remarks for what counts as a difference. <paramref name="startLine"/> is the 1-based file line of
    /// <paramref name="actualLines"/>[0].
    /// </summary>
    internal static IReadOnlyList<SnippetDifference> ComputeDifferences(
        SourceText sourceText, string snippet, int startLine, IReadOnlyList<string> actualLines)
    {
        var differences = new List<SnippetDifference>();
        var content = SplitContent(snippet, out var leadingDropped);
        if (content.Count == 0 || actualLines.Count == 0)
        {
            return differences;
        }

        if (content.Count != actualLines.Count)
        {
            // A multi-line snippet the collapsed single-line stage matched inside fewer file lines: the line structure
            // itself differs, so there is no pairing to report line by line.
            differences.Add(new SnippetDifference(
                SnippetDifferenceKind.InteriorWhitespace, leadingDropped + 1,
                string.Join("\n", content.Select(p => p.Text)), actualLines[0], startLine, actualLines[0]));
            return differences;
        }

        for (var j = 0; j < content.Count; j++)
        {
            var snippetLine = leadingDropped + j + 1;
            var fileLine = startLine + j;
            var isFirst = j == 0;
            var isLast = j == content.Count - 1;
            DiffLine(content[j].Text, actualLines[j], isFirst, isLast, snippetLine, fileLine, differences);

            if (!isLast)
            {
                var fileEnding = LineEndingAfter(sourceText, fileLine - 1);
                var snippetEnding = content[j].Ending;
                if (snippetEnding.Length > 0 && fileEnding.Length > 0 && snippetEnding != fileEnding)
                {
                    differences.Add(new SnippetDifference(
                        SnippetDifferenceKind.LineEnding, snippetLine,
                        EndingName(snippetEnding), EndingName(fileEnding), fileLine, actualLines[j]));
                }
            }
        }

        return differences;
    }

    /// <summary>Closest windows of a source text; see the type remarks. Empty for a blank snippet or an empty text.</summary>
    internal static ClosestWindowSearch FindClosestWindows(SourceText sourceText, string snippet, SnippetCaseMode caseMode)
    {
        var lines = new string[sourceText.Lines.Count];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = sourceText.Lines[i].ToString();
        }

        return FindClosestWindows(lines, snippet, caseMode);
    }

    /// <summary>Closest windows over the file's lines (verbatim, without line breaks); see the type remarks.</summary>
    internal static ClosestWindowSearch FindClosestWindows(IReadOnlyList<string> fileLines, string snippet, SnippetCaseMode caseMode)
    {
        var content = SplitContent(snippet, out _);
        if (content.Count == 0 || fileLines.Count == 0)
        {
            return new ClosestWindowSearch(Array.Empty<SnippetClosestWindow>(), 0, 0);
        }

        var needle = content.Select(p => Normalize(p.Text, caseMode)).ToArray();
        var file = fileLines.Select(l => Normalize(l, caseMode)).ToArray();
        var weights = needle.Select(n => (double)Math.Max(1, n.Length)).ToArray();
        var totalWeight = weights.Sum();

        var qualifying = new List<(int Start, double Score)>();
        for (var p = 0; p < file.Length; p++)
        {
            var score = ScoreWindow(file, needle, weights, totalWeight, p);
            if (score >= MinClosestWindowScore)
            {
                qualifying.Add((p, score));
            }
        }

        // Rank first, then truncate: the best window may lie beyond the cap in file order.
        var windows = qualifying
            .OrderByDescending(w => w.Score)
            .ThenBy(w => w.Start)
            .Take(MaxClosestWindows)
            .Select(w => new SnippetClosestWindow(
                w.Start + 1,
                Enumerable.Range(w.Start, Math.Min(needle.Length, file.Length - w.Start)).Select(i => fileLines[i]).ToList(),
                w.Score))
            .ToList();

        return new ClosestWindowSearch(windows, qualifying.Count, file.Length);
    }

    private static double ScoreWindow(string[] file, string[] needle, double[] weights, double totalWeight, int start)
    {
        var earned = 0.0;
        var i = 0;
        while (i < needle.Length && start + i < file.Length)
        {
            var alignment = LineBlockAligner.FindBestPrefixAlignment(
                file, new ArraySegment<string>(needle, i, needle.Length - i), new[] { start + i });
            for (var k = 0; k < alignment.MatchedCount; k++)
            {
                earned += weights[i + k];
            }

            i += alignment.MatchedCount;
            if (i < needle.Length && start + i < file.Length)
            {
                earned += weights[i] * Math.Min(Similarity(needle[i], file[start + i]), MaxPartialCredit);
                i++;
            }
        }

        return earned / totalWeight;
    }

    private static string Normalize(string line, SnippetCaseMode caseMode)
    {
        var collapsed = Regex.Replace(line.Trim(), @"\s+", " ");
        return caseMode == SnippetCaseMode.CaseInsensitive ? collapsed.ToUpperInvariant() : collapsed;
    }

    // Dice coefficient over character bigrams (multiset), 0..1. Texts shorter than two characters are equal or not.
    private static double Similarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return a == b ? 1.0 : 0.0;
        }

        var counts = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var key = (a[i] << 16) | a[i + 1];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var key = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(key, out var remaining) && remaining > 0)
            {
                counts[key] = remaining - 1;
                shared++;
            }
        }

        return 2.0 * shared / ((a.Length - 1) + (b.Length - 1));
    }

    // Splits a snippet into lines with the line break that follows each, then drops fully blank leading and trailing
    // lines (as the matchers do). leadingDropped is how many were dropped from the front, so reported snippet line
    // numbers refer to the snippet as the caller sent it.
    private static List<SnippetLinePart> SplitContent(string snippet, out int leadingDropped)
    {
        var parts = new List<SnippetLinePart>();
        var start = 0;
        var i = 0;
        while (i < snippet.Length)
        {
            var c = snippet[i];
            if (c == '\r' || c == '\n')
            {
                var ending = c == '\r' && i + 1 < snippet.Length && snippet[i + 1] == '\n' ? "\r\n" : c.ToString();
                parts.Add(new SnippetLinePart(snippet[start..i], ending));
                i += ending.Length;
                start = i;
            }
            else
            {
                i++;
            }
        }

        parts.Add(new SnippetLinePart(snippet[start..], string.Empty));

        var first = parts.FindIndex(p => !string.IsNullOrWhiteSpace(p.Text));
        if (first < 0)
        {
            leadingDropped = 0;
            return new List<SnippetLinePart>();
        }

        var last = parts.FindLastIndex(p => !string.IsNullOrWhiteSpace(p.Text));
        leadingDropped = first;
        return parts.GetRange(first, last - first + 1);
    }

    private static string LineEndingAfter(SourceText sourceText, int zeroBasedLine)
    {
        if (zeroBasedLine < 0 || zeroBasedLine >= sourceText.Lines.Count)
        {
            return string.Empty;
        }

        var line = sourceText.Lines[zeroBasedLine];
        return sourceText.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
    }

    private static string EndingName(string ending) => ending switch
    {
        "\r\n" => "CRLF",
        "\n" => "LF",
        _ => "CR",
    };

    // Same rule as the exact matcher: the first line may start mid-line (the file line only has to end with it), the last
    // may stop mid-line (the file line only has to start with it), every other line must be equal.
    private static bool IsExactCompatible(string snippetLine, string fileLine, bool isFirst, bool isLast)
    {
        if (isFirst && isLast)
        {
            return fileLine.Contains(snippetLine, StringComparison.Ordinal);
        }

        if (isFirst)
        {
            return fileLine.EndsWith(snippetLine, StringComparison.Ordinal);
        }

        return isLast
            ? fileLine.StartsWith(snippetLine, StringComparison.Ordinal)
            : string.Equals(fileLine, snippetLine, StringComparison.Ordinal);
    }

    private static void DiffLine(
        string snippetLine, string fileLine, bool isFirst, bool isLast, int snippetLineNumber, int fileLineNumber,
        List<SnippetDifference> differences)
    {
        if (IsExactCompatible(snippetLine, fileLine, isFirst, isLast))
        {
            return;
        }

        var snippetTrimmed = snippetLine.Trim();
        var fileTrimmed = fileLine.Trim();
        var added = false;

        void Add(SnippetDifferenceKind kind, string expected, string got)
        {
            differences.Add(new SnippetDifference(kind, snippetLineNumber, expected, got, fileLineNumber, fileLine));
            added = true;
        }

        var snippetLead = LeadingWhitespace(snippetLine);
        var fileLead = LeadingWhitespace(fileLine);
        if (snippetLead != fileLead && !(isFirst && snippetLead.Length == 0))
        {
            Add(SnippetDifferenceKind.LeadingWhitespace, snippetLead, fileLead);
        }

        var snippetTrail = TrailingWhitespace(snippetLine);
        var fileTrail = TrailingWhitespace(fileLine);
        if (snippetTrail != fileTrail && !(isLast && snippetTrail.Length == 0))
        {
            Add(SnippetDifferenceKind.TrailingWhitespace, snippetTrail, fileTrail);
        }

        if (!TrimmedCoreEquals(snippetTrimmed, fileTrimmed, isFirst, isLast))
        {
            Add(SnippetDifferenceKind.InteriorWhitespace, snippetTrimmed, fileTrimmed);
        }

        if (!added)
        {
            // Incompatible with the exact matcher but no edge or interior cause found: report the whole line rather
            // than stay silent about a line that differs.
            Add(SnippetDifferenceKind.InteriorWhitespace, snippetLine, fileLine);
        }
    }

    // Whether the trimmed texts agree under the same fragment rule, so what is left to report is edge whitespace.
    private static bool TrimmedCoreEquals(string snippetTrimmed, string fileTrimmed, bool isFirst, bool isLast)
    {
        if (isFirst && isLast)
        {
            return fileTrimmed.Contains(snippetTrimmed, StringComparison.Ordinal);
        }

        if (isFirst)
        {
            return fileTrimmed.EndsWith(snippetTrimmed, StringComparison.Ordinal);
        }

        return isLast
            ? fileTrimmed.StartsWith(snippetTrimmed, StringComparison.Ordinal)
            : snippetTrimmed == fileTrimmed;
    }

    private static string LeadingWhitespace(string line) => line[..(line.Length - line.TrimStart().Length)];

    // A whitespace-only line has all of its whitespace as leading whitespace; it has no trailing whitespace of its own.
    private static string TrailingWhitespace(string line)
        => line.Trim().Length == 0 ? string.Empty : line[line.TrimEnd().Length..];
}
