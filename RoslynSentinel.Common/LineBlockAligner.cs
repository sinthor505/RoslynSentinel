namespace RoslynSentinel.Common;

internal static class LineBlockAligner
{
    /// <summary>
    /// Describes where a partial match diverged: the first needle line that did not match after
    /// <c>best.MatchedCount</c> lines, and the file line found in its place (or end of file).
    /// Only meaningful when <c>best.MatchedCount</c> is greater than 0.
    /// </summary>
    internal static string DescribeDivergence(IReadOnlyList<string> lines, IReadOnlyList<string> needleLines, Alignment best)
    {
        var mismatchLineNumber = best.Position + best.MatchedCount + 1;
        return $"Matched the first {best.MatchedCount} anchor line(s) starting at line {best.Position + 1}, " +
               $"but then expected \"{needleLines[best.MatchedCount]}\" at line {mismatchLineNumber} - found " +
               $"\"{(best.Position + best.MatchedCount < lines.Count ? lines[best.Position + best.MatchedCount].Trim() : "<end of file>")}\" " +
               "instead.";
    }

    /// <summary>
    /// Returns how many leading <paramref name="anchorLines"/> match the file starting at
    /// <paramref name="start"/> before the first divergence (or the file/list boundary) ->
    /// <c>anchorLines.Count</c> means a full match.
    /// </summary>
    private static int MatchLeadingCount(IReadOnlyList<string> lines, IReadOnlyList<string> anchorLines, int start)
    {
        if (start < 0)
        {
            return 0;
        }

        for (int j = 0; j < anchorLines.Count; j++)
        {
            if (start + j >= lines.Count || lines[start + j].Trim() != anchorLines[j])
            {
                return j;
            }
        }
        return anchorLines.Count;
    }

    /// <summary>
    /// Scans <paramref name="orderedCandidates"/> in the order given and returns the position whose
    /// leading run of <paramref name="needleLines"/> matches <paramref name="lines"/> the longest.
    /// Ties go to the earlier candidate; the first candidate that fully matches wins immediately.
    /// If nothing matches at all, Position is the first candidate (0 if there are none) and MatchedCount is 0.
    /// </summary>
    internal static Alignment FindBestPrefixAlignment(IReadOnlyList<string> lines, IReadOnlyList<string> needleLines, IEnumerable<int> orderedCandidates)
    {
        var best = new Alignment(0, 0, false);
        var first = true;
        foreach (var position in orderedCandidates)
        {
            if (first)
            {
                best = new Alignment(position, 0, false);
                first = false;
            }

            var matchedCount = MatchLeadingCount(lines, needleLines, position);
            if (matchedCount == needleLines.Count)
            {
                return new Alignment(position, matchedCount, true);
            }

            if (matchedCount > best.MatchedCount)
            {
                best = new Alignment(position, matchedCount, false);
            }
        }

        return best;
    }
}
/// <summary>
/// Result of <see cref="LineBlockAligner.FindBestPrefixAlignment"/>: where the best candidate
/// was, how many leading needle lines matched there, and whether that was a full match.
/// </summary>
internal readonly record struct Alignment(int Position, int MatchedCount, bool IsFullMatch);
