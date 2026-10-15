using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

/// <summary>Why a <see cref="CollateralChangeViolation"/> was reported.</summary>
public enum CollateralChangeKind
{
    /// <summary>The text about to be written differs from the pre-image plus the declared edits.</summary>
    ContentDiffers,

    /// <summary>The declared edits cannot be applied to the pre-image (they overlap or fall outside it), so nothing could be compared.</summary>
    EditsNotApplicable
}

/// <summary>
/// One existing file whose proposed content is not exactly its pre-image plus the edits the producing
/// operation declared. <see cref="Line"/> and <see cref="Column"/> are 1-based and locate the first
/// differing character (0 for <see cref="CollateralChangeKind.EditsNotApplicable"/>);
/// <see cref="Expected"/> is the pre-image-plus-edits line there and <see cref="Actual"/> the proposed text's line,
/// each capped (see <see cref="CollateralChangeGuard.MaxExcerptChars"/>) with CR and LF shown as the escapes \r and \n.
/// </summary>
public record CollateralChangeViolation(
    string FilePath,
    CollateralChangeKind Kind,
    int Line,
    int Column,
    string Expected,
    string Actual);

/// <summary>
/// Write-path tripwire: an operation that reports the text edits it made must produce, for each file it edits,
/// exactly the pre-image with those edits applied. Any other difference is collateral damage from a step that ran
/// after the edits were built (a whole-file reformat or normalize, for example). It cannot catch a wrong edit set,
/// because the proposed text and the edits come from the same producer; it catches post-processing only.
/// Pure: no I/O, no logging. The caller decides how to refuse (see <see cref="ToolErrorCode.CollateralChangeRefused"/>).
/// </summary>
public static class CollateralChangeGuard
{
    /// <summary>Maximum characters of a line quoted in a violation, so a refusal never dumps source.</summary>
    public const int MaxExcerptChars = 120;

    /// <summary>
    /// Applies <paramref name="edits"/> (positions in <paramref name="preImage"/>) the way the MoveMember engine's
    /// <c>MoveMemberTextEdits.ApplyChanges</c> does: ordered by span start, then span length (stable), then
    /// <see cref="SourceText.WithChanges(IEnumerable{TextChange})"/>. Throws <see cref="ArgumentException"/> when the edits
    /// overlap or fall outside the text.
    /// </summary>
    public static string ApplyEdits(string preImage, IReadOnlyList<TextChange> edits)
    {
        var ordered = edits.OrderBy(c => c.Span.Start).ThenBy(c => c.Span.Length).ToList();
        return ordered.Count == 0 ? preImage : SourceText.From(preImage).WithChanges(ordered).ToString();
    }

    /// <summary>
    /// Returns null when <paramref name="proposedText"/> is exactly <paramref name="preImage"/> with
    /// <paramref name="edits"/> applied; otherwise a violation naming the first differing line. Only existing files
    /// have a pre-image, so a caller skips a new file (it has nothing to compare against). Comparison is ordinal
    /// and exact, including line endings and the final newline; there is no tolerance.
    /// </summary>
    public static CollateralChangeViolation? Check(
        string filePath, string preImage, string proposedText, IReadOnlyList<TextChange> edits)
    {
        string expected;
        try
        {
            expected = ApplyEdits(preImage, edits);
        }
        catch (ArgumentException)
        {
            return new CollateralChangeViolation(filePath, CollateralChangeKind.EditsNotApplicable, 0, 0, string.Empty, string.Empty);
        }

        if (string.Equals(expected, proposedText, StringComparison.Ordinal))
        {
            return null;
        }

        // The texts share their first `index` characters, so the line start, line number and column are the same in both.
        var index = FirstDifference(expected, proposedText);
        var lineStart = index == 0 ? 0 : expected.LastIndexOf('\n', index - 1) + 1;
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (expected[i] == '\n')
            {
                line++;
            }
        }

        var column = index - lineStart + 1;
        return new CollateralChangeViolation(
            filePath,
            CollateralChangeKind.ContentDiffers,
            line,
            column,
            Excerpt(expected, lineStart, column),
            Excerpt(proposedText, lineStart, column));
    }

    /// <summary>Actionable message: names each file and first differing line, the likely cause, and the outcome.</summary>
    public static string BuildMessage(string operationName, IReadOnlyList<CollateralChangeViolation> violations)
    {
        var files = string.Join("; ", violations.Select(v => v.Kind == CollateralChangeKind.EditsNotApplicable
            ? $"'{v.FilePath}' (the declared edits overlap or fall outside the file)"
            : $"'{v.FilePath}' (first difference at line {v.Line}, column {v.Column}; expected `{v.Expected}`, actual `{v.Actual}`)"));
        return $"{operationName}: refused - the text to be written differs from the file plus the edits the operation declared in {violations.Count} existing file(s): {files}. " +
               "No files were written. A change must only touch what its edits touch, so this almost always means a step after the edits " +
               "were built (a whole-file reformat or NormalizeWhitespace) rewrote more of the file than the operation intended. " +
               "Re-issue the operation; if it keeps happening the producing tool has a defect to report.";
    }

    private static int FirstDifference(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// The line of <paramref name="text"/> starting at <paramref name="lineStart"/>, including its terminator, capped to a
    /// window around <paramref name="column"/>, with CR and LF rendered as escapes so end-of-line and final-newline
    /// differences are visible. "&lt;end of text&gt;" when the text has no characters from <paramref name="lineStart"/> on.
    /// </summary>
    private static string Excerpt(string text, int lineStart, int column)
    {
        if (lineStart >= text.Length)
        {
            return "<end of text>";
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        var end = lineEnd < 0 ? text.Length : lineEnd + 1;
        var raw = text.Substring(lineStart, end - lineStart);

        var prefix = string.Empty;
        var suffix = string.Empty;
        if (raw.Length > MaxExcerptChars)
        {
            var start = Math.Max(0, column - 1 - MaxExcerptChars / 3);
            var take = Math.Min(MaxExcerptChars, raw.Length - start);
            prefix = start > 0 ? "..." : string.Empty;
            suffix = start + take < raw.Length ? "..." : string.Empty;
            raw = raw.Substring(start, take);
        }

        return prefix + raw.Replace("\r", "\\r").Replace("\n", "\\n") + suffix;
    }
}
