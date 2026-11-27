using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// The four match stages that the matchers and the legacy <see cref="ContextHelper"/> snippet helpers share,
/// moved here unchanged from <see cref="ContextHelper"/>. They still return the legacy
/// <see cref="SnippetSpan"/> (start and length only); the matchers convert to the new
/// <see cref="SnippetMatch"/>. Stage logic must not change here without a behaviour-change slice.
/// </summary>
internal static class SnippetStages
{
    /// <summary>
    /// Stage 1 of the legacy ContextHelper matchers (removed):
    /// every ordinal (case-sensitive, byte-exact) occurrence of <paramref name="contextSnippet"/> in
    /// <paramref name="source"/>, overlapping occurrences included. Each span's length is
    /// <c>contextSnippet.Length</c>, which is the real length because the comparison is ordinal. The caller
    /// must pass a non-whitespace snippet (an empty snippet would match at every offset).
    /// </summary>
    internal static List<SnippetSpan> FindOrdinalStage(string source, string contextSnippet)
    {
        var matches = new List<SnippetSpan>();
        int idx = 0;
        while ((idx = source.IndexOf(contextSnippet, idx, StringComparison.Ordinal)) >= 0)
        {
            matches.Add(new SnippetSpan(idx, contextSnippet.Length));
            idx++;
        }

        return matches;
    }

    /// <summary>
    /// Stage 2 of the legacy ContextHelper matchers (removed):
    /// a multi-line snippet matched with CR, LF and CRLF treated as interchangeable, and nothing else loosened
    /// (case-sensitive). Returns an empty list for a snippet with no '\n' (the stage only exists for a multi-line
    /// snippet). Each span is the real regex match, so its length can differ from <c>contextSnippet.Length</c>
    /// when the file's line endings differ from the snippet's.
    /// </summary>
    internal static List<SnippetSpan> FindLineEndingStage(string source, string contextSnippet)
    {
        var matches = new List<SnippetSpan>();
        if (!contextSnippet.Contains('\n'))
        {
            return matches;
        }

        // A multi-line snippet failed the exact ordinal search -> the likely cause is a
        // line-ending mismatch (e.g. the caller composed the snippet with \n while the file
        // on disk is \r\n, or vice versa), not a genuine content difference. Retry treating
        // any \r\n/\r/\n in the snippet as interchangeable with whatever the source actually
        // uses, without loosening any other whitespace -> a caller building a multi-statement
        // selection out of literal source lines should not need to know the file's line-ending
        // convention.
        var pattern = string.Join(@"\r?\n",
            contextSnippet.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
                .Select(System.Text.RegularExpressions.Regex.Escape));
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(source, pattern))
        {
            matches.Add(new SnippetSpan(m.Index, m.Length));
        }

        return matches;
    }

    /// <summary>
    /// Stage 3 of the legacy ContextHelper matchers (removed): the snippet, whitespace-collapsed, searched as a
    /// substring of each source line (also whitespace-collapsed). Works for a single-line snippet and, because the
    /// snippet's newlines collapse to spaces, can also match a multi-line snippet against one line. Each span is
    /// mapped back to the real line text, so its length is NOT <c>contextSnippet.Length</c>. Today's callers pass
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    internal static List<SnippetSpan> FindCollapsedSingleLineStage(
        SourceText sourceText, string contextSnippet, StringComparison comparison)
    {
        var matches = new List<SnippetSpan>();

        // Fallback: try matching with collapsed whitespace. This handles a single-line
        // snippet whose indentation doesn't match the source (common when an AI caller
        // retypes a line from memory instead of copying it verbatim character-for-character ->
        // models reliably reproduce tokens but not incidental indentation).
        var snippetNorm = System.Text.RegularExpressions.Regex.Replace(contextSnippet.Trim(), @"\s+", " ");
        var lines = sourceText.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            var lineText = lines[i].ToString();
            var lineNorm = System.Text.RegularExpressions.Regex.Replace(lineText.Trim(), @"\s+", " ");
            var normIndex = lineNorm.IndexOf(snippetNorm, comparison);
            if (normIndex >= 0)
            {
                // Map the match offset within the whitespace-collapsed line back to the
                // corresponding offset in the real (pre-collapse) line text, so callers that
                // need in-line precision (e.g. ExtractLocalVariableAsync locating an
                // ExpressionSyntax's SpanStart) land on the actual match, not just "somewhere
                // in the right line" -> the line-start position was fine for member/type
                // resolution (FindNode/AncestorsAndSelf walk up to the enclosing declaration
                // regardless of exact column) but wrong for expression-level lookups.
                //
                // realEnd is mapped independently of realStart (not realStart + snippetNorm.Length)
                // because the raw line can have a different amount of whitespace than the
                // collapsed snippet (e.g. source "a  +  b" vs snippet "a + b") -> the real span's
                // length is realEnd - realStart, which is NOT contextSnippet.Length. A caller that
                // assumed contextSnippet.Length here is what corrupted BuildResult.cs via
                // ReplaceSnippet (see project_replacesnippet_silent_splice_corruption_adjacent_lines).
                var realStart = MapNormalizedOffsetToRaw(lineText, normIndex);
                var realEnd = MapNormalizedOffsetToRaw(lineText, normIndex + snippetNorm.Length);
                matches.Add(new SnippetSpan(lines[i].Start + realStart, realEnd - realStart));
            }
        }

        return matches;
    }

    /// <summary>
    /// Stage 4 of the legacy ContextHelper matchers (removed): a multi-line snippet matched against a window of the
    /// same number of consecutive source lines, each line trimmed and the window's whitespace runs collapsed.
    /// Returns an empty list for a snippet with no '\n' (the stage only exists for a multi-line snippet).
    /// Each span starts at the window's first line start (so it includes that line's indentation) and ends at the
    /// last line's end excluding its line break; its length is NOT <c>contextSnippet.Length</c>. Today's callers pass
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    internal static List<SnippetSpan> FindCollapsedWindowStage(
        SourceText sourceText, string contextSnippet, StringComparison comparison)
    {
        var matches = new List<SnippetSpan>();
        if (!contextSnippet.Contains('\n'))
        {
            return matches;
        }

        // Fallback: same whitespace-collapse tolerance as above, but for a snippet spanning
        // multiple statements/lines. The single-line fallback can never match this shape ->
        // it only ever tests one source line against the whole (newline-collapsed) snippet.
        // Instead, slide a window of N consecutive source lines (N = the snippet's own line
        // count), collapse whitespace runs on both sides identically, and compare -> this
        // preserves line-by-line structure (so it won't match reordered statements) while
        // being indifferent to indentation depth, which carries no compiler meaning in C#.
        //
        // A caller-supplied snippet very commonly ends (and sometimes starts) with a blank
        // line -> e.g. "return foo;\n}\n" from copying a whole statement plus its closing brace
        // with a trailing newline. Split('\n') on that produces a trailing empty-string
        // element, which would otherwise inflate windowSize by one and force the window to
        // swallow one real, unrelated source line that was never meant to be part of the
        // match. Trim blank leading/trailing lines before computing windowSize so the window
        // reflects only the snippet's actual content lines.
        var snippetLineTexts = SnippetMatcherSupport.SplitSnippetIntoTrimmedContentLines(contextSnippet);
        var snippetWindowNorm = System.Text.RegularExpressions.Regex.Replace(
            string.Join("\n", snippetLineTexts.Select(l => l.Trim())).Trim(), @"\s+", " ");
        var lines = sourceText.Lines;
        int windowSize = snippetLineTexts.Length;

        for (int i = 0; i + windowSize <= lines.Count; i++)
        {
            var windowStart = lines[i].Start;
            var windowText = string.Join(
                "\n", Enumerable.Range(i, windowSize).Select(j => lines[j].ToString().Trim()));
            var windowNorm = System.Text.RegularExpressions.Regex.Replace(windowText, @"\s+", " ");
            if (windowNorm.Equals(snippetWindowNorm, comparison))
            {
                // The window's real length runs to the end of its last line's own text
                // (excluding that line's trailing line break), not EndIncludingLineBreak ->
                // matching FindSnippetPosition's historical start-of-line-only precision
                // while still giving an accurate removable span. Like the single-line fallback
                // above, this length is derived from the actual source lines, NOT
                // contextSnippet.Length -> the two diverge whenever indentation/spacing differs
                // between the caller's snippet and the real file content.
                var realEnd = lines[i + windowSize - 1].End;
                matches.Add(new SnippetSpan(windowStart, realEnd - windowStart));
            }
        }

        return matches;
    }

    /// <summary>
    /// Maps a character offset within a whitespace-collapsed, trimmed line (every run of
    /// whitespace replaced by a single space) back to the corresponding offset in the original,
    /// untrimmed line text. Used to recover a precise match position after
    /// the legacy ContextHelper matcher's (removed) whitespace-tolerant fallback locates a snippet inside
    /// the collapsed form.
    /// </summary>
    private static int MapNormalizedOffsetToRaw(string rawLine, int normalizedOffset)
    {
        var leadingWhitespace = rawLine.Length - rawLine.TrimStart().Length;
        int rawIndex = leadingWhitespace;
        int normIndex = 0;

        while (rawIndex < rawLine.Length && normIndex < normalizedOffset)
        {
            if (char.IsWhiteSpace(rawLine[rawIndex]))
            {
                // A whole run of raw whitespace collapses to a single normalized space -> consume
                // the entire run in one step so rawIndex lands on the next real character, not
                // partway through the run.
                normIndex++;
                while (rawIndex < rawLine.Length && char.IsWhiteSpace(rawLine[rawIndex]))
                {
                    rawIndex++;
                }
            }
            else
            {
                normIndex++;
                rawIndex++;
            }
        }

        return rawIndex;
    }
}
