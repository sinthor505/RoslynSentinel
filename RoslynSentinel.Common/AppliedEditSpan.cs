using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// One edit the engine applied to a file: start and length in the PRE-IMAGE text, plus the replacement text (capped).
/// </summary>
public record AppliedEditSpan(string FilePath, int Start, int Length, string NewText, bool NewTextTruncated = false)
{
    /// <summary>
    /// Flattens applied edits into spans. Returns null when there are no edits. Capped at
    /// <paramref name="maxSpans"/> spans in total and <paramref name="maxNewTextChars"/> characters of new text per span.
    /// </summary>
    public static List<AppliedEditSpan>? FromEdits(IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? edits, int maxSpans = 100, int maxNewTextChars = 200)
    {
        if (edits == null)
        {
            return null;
        }

        var spans = new List<AppliedEditSpan>();
        foreach (var (file, changes) in edits)
        {
            foreach (var change in changes.OrderBy(c => c.Span.Start))
            {
                if (spans.Count >= maxSpans)
                {
                    return spans;
                }

                var newText = change.NewText ?? string.Empty;
                var truncated = newText.Length > maxNewTextChars;
                spans.Add(new AppliedEditSpan(file, change.Span.Start, change.Span.Length, truncated ? newText[..maxNewTextChars] : newText, truncated));
            }
        }

        return spans.Count == 0 ? null : spans;
    }
}
