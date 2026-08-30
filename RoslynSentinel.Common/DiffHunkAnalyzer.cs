using System.Text;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Common;

/// <summary>
/// Diagnoses a raw unified-diff string's hunks against known failure patterns from
/// model-generated diffs — wrong header line counts, phantom trailing-blank anchor lines, missing
/// "-" lines, and stale/mismatched line numbers. <see cref="DiffEngine.ApplyDiff"/> runs this on
/// every call (see its <c>diagnosticLog</c> parameter) so a failure comes with a ready-made report
/// instead of requiring the kind of manual line-by-line archaeology that root-caused the original
/// bug this class exists to catch automatically (see docs/current/blockers).
/// </summary>
public static class DiffHunkAnalyzer
{
    private static readonly string[] SeparatorArray = new[] { "\r\n", "\r", "\n" };
    private static readonly Regex HunkHeaderRegex = new(@"^@@\s+\-(\d+),?(\d*)\s+\+(\d+),?(\d*)\s+@@");

    public sealed record HunkReport(
        string Header,
        int DeclaredOldStart,
        int DeclaredOldCount,
        int DeclaredNewStart,
        int DeclaredNewCount,
        int ActualContextLines,
        int ActualRemovalLines,
        int ActualAdditionLines,
        int ActualOldCount,
        int ActualNewCount,
        bool HeaderCountsMatchBody,
        bool EndsWithUnmarkedBlankLine)
    {
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Header);
            if (!HeaderCountsMatchBody)
            {
                sb.Append($" [HEADER COUNT MISMATCH: declared old={DeclaredOldCount}/new={DeclaredNewCount}, " +
                          $"actual old={ActualOldCount}/new={ActualNewCount} " +
                          $"(context={ActualContextLines}, -={ActualRemovalLines}, +={ActualAdditionLines})]");
            }
            if (EndsWithUnmarkedBlankLine)
            {
                sb.Append(" [ends with an unmarked blank line — ambiguous between a real blank " +
                          "context line and a diff-text trailing-newline artifact]");
            }
            return sb.ToString();
        }
    }

    public sealed record DiffReport(int HunkCount, IReadOnlyList<HunkReport> Hunks, IReadOnlyList<string> MalformedLines)
    {
        public bool HasFindings => Hunks.Any(h => !h.HeaderCountsMatchBody || h.EndsWithUnmarkedBlankLine) || MalformedLines.Count > 0;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"DiffHunkAnalyzer: {HunkCount} hunk(s).");
            foreach (var hunk in Hunks)
            {
                sb.Append("\n  ").Append(hunk.Describe());
            }
            foreach (var malformed in MalformedLines)
            {
                sb.Append("\n  [UNRECOGNIZED LINE, no +/-/space marker]: ").Append(malformed);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Parses <paramref name="unifiedDiff"/> hunk-by-hunk and reports, for each one, the header's
    /// declared old/new counts alongside what the body actually contains. A mismatch here is what
    /// caused the original bug this class was built to catch: a model-generated hunk header
    /// claiming 7 old-side lines when the body only had 3 does not, on its own, break anything —
    /// but it is exactly the kind of drift that makes any future count-trusting change to
    /// <see cref="DiffEngine"/> unsafe, so it is worth surfacing even when the apply itself
    /// succeeds via the structural (blank-line/next-header) scan.
    /// </summary>
    public static DiffReport Analyze(string unifiedDiff)
    {
        var diffLines = unifiedDiff.Split(SeparatorArray, StringSplitOptions.None);
        var hunks = new List<HunkReport>();
        var malformedLines = new List<string>();

        for (int i = 0; i < diffLines.Length; i++)
        {
            var match = HunkHeaderRegex.Match(diffLines[i]);
            if (!match.Success)
            {
                continue;
            }

            int declaredOldStart = int.Parse(match.Groups[1].Value);
            int declaredOldCount = match.Groups[2].Value.Length == 0 ? 1 : int.Parse(match.Groups[2].Value);
            int declaredNewStart = int.Parse(match.Groups[3].Value);
            int declaredNewCount = match.Groups[4].Value.Length == 0 ? 1 : int.Parse(match.Groups[4].Value);

            int contextLines = 0;
            int removalLines = 0;
            int additionLines = 0;
            bool endsWithUnmarkedBlank = false;

            int j = i + 1;
            for (; j < diffLines.Length && !HunkHeaderRegex.IsMatch(diffLines[j]); j++)
            {
                var line = diffLines[j];
                bool isLastBeforeBoundary = j == diffLines.Length - 1
                    || (j + 1 < diffLines.Length && HunkHeaderRegex.IsMatch(diffLines[j + 1]));

                if (line.Length == 0 && isLastBeforeBoundary)
                {
                    // Structural terminator (diff-end or next-hunk separator), not hunk content —
                    // matches DiffEngine.ReadHunkBody's own boundary logic exactly.
                    break;
                }

                if (line.StartsWith("+"))
                {
                    additionLines++;
                }
                else if (line.StartsWith("-"))
                {
                    removalLines++;
                }
                else if (line.StartsWith(" ") || line.Length == 0)
                {
                    contextLines++;
                    if (line.Length == 0 && j == diffLines.Length - 1)
                    {
                        endsWithUnmarkedBlank = true;
                    }
                }
                else
                {
                    malformedLines.Add(line);
                }
            }

            int actualOldCount = contextLines + removalLines;
            int actualNewCount = contextLines + additionLines;

            hunks.Add(new HunkReport(
                Header: match.Value,
                DeclaredOldStart: declaredOldStart,
                DeclaredOldCount: declaredOldCount,
                DeclaredNewStart: declaredNewStart,
                DeclaredNewCount: declaredNewCount,
                ActualContextLines: contextLines,
                ActualRemovalLines: removalLines,
                ActualAdditionLines: additionLines,
                ActualOldCount: actualOldCount,
                ActualNewCount: actualNewCount,
                HeaderCountsMatchBody: actualOldCount == declaredOldCount && actualNewCount == declaredNewCount,
                EndsWithUnmarkedBlankLine: endsWithUnmarkedBlank));

            i = j - 1;
        }

        return new DiffReport(hunks.Count, hunks, malformedLines);
    }
}
