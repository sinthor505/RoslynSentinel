using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

public static class EolUtilities
{
    /// <summary>Detects the dominant line-ending style in a string: CRLF (\r\n), LF (\n), or CR (\r).</summary>
    public static string DetectDominantEol(string content)
    {
        int crlfCount = 0, lfCount = 0, crCount = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    crlfCount++;
                    i++; // skip the \n
                }
                else
                {
                    crCount++;
                }
            }
            else if (content[i] == '\n')
            {
                lfCount++;
            }
        }

        // Tie-break: CRLF, then LF, then CR
        if (crlfCount > 0 && crlfCount >= lfCount && crlfCount >= crCount)
        {
            return "\r\n";
        }

        if (lfCount > 0)
        {
            return "\n";
        }

        if (crCount > 0)
        {
            return "\r";
        }

        return Environment.NewLine; // no line breaks found
    }

    public static string DetectDominantEol(SourceText sourceText)
    {
        var endings = sourceText.Lines
            .Select(l => sourceText.GetSubText(TextSpan.FromBounds(l.End, l.EndIncludingLineBreak)).ToString())
            .ToList();
        return endings
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? Environment.NewLine;
    }

    public static string NormalizeEol(string content, string dominantEol)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return dominantEol == "\n" ? normalized : normalized.Replace("\n", dominantEol);
    }

    /// <summary>
    /// Normalizes line endings the way a write to an EXISTING file should: only the lines the change added or
    /// rewrote take <paramref name="dominantEol"/>; every line that survives from <paramref name="before"/> keeps its
    /// own original terminator, and a removed line takes its terminator with it. This differs from
    /// <see cref="NormalizeEol"/>, which rewrites the whole content and so silently turns the stray lines of a
    /// mixed-EOL file (e.g. two CRLF lines in an otherwise-LF file) into the dominant style - a change
    /// <see cref="EolChangeGuard"/> classifies as "no EOL change" on the way in but refuses on the way back out.
    /// Lines are matched by text (terminator excluded) with a common prefix/suffix trim and an LCS over the
    /// differing middle; a middle too large for the LCS table is treated as entirely changed. A matched line keeps the
    /// pre-image terminator unless one side has none (final line without a newline), in which case the proposed side
    /// decides (absent stays absent, present becomes <paramref name="dominantEol"/>). For a single-style pre-image
    /// that passed <see cref="EolChangeGuard"/> the result equals <see cref="NormalizeEol"/>. A null
    /// <paramref name="before"/> (new file) falls back to <see cref="NormalizeEol"/>.
    /// </summary>
    public static string NormalizeEolOfChangedLines(string? before, string after, string dominantEol)
    {
        if (before is null)
        {
            return NormalizeEol(after, dominantEol);
        }

        var oldLines = SplitLinesWithEol(before);
        var newLines = SplitLinesWithEol(after);

        // match[j] = index of the pre-image line that new line j is, or -1 when the line is added/rewritten.
        var match = new int[newLines.Count];
        Array.Fill(match, -1);

        int prefix = 0;
        int maxPrefix = Math.Min(oldLines.Count, newLines.Count);
        while (prefix < maxPrefix && oldLines[prefix].Text == newLines[prefix].Text)
        {
            match[prefix] = prefix;
            prefix++;
        }

        int suffix = 0;
        int maxSuffix = maxPrefix - prefix;
        while (suffix < maxSuffix &&
               oldLines[oldLines.Count - 1 - suffix].Text == newLines[newLines.Count - 1 - suffix].Text)
        {
            match[newLines.Count - 1 - suffix] = oldLines.Count - 1 - suffix;
            suffix++;
        }

        MatchMiddle(oldLines, prefix, oldLines.Count - suffix, newLines, prefix, newLines.Count - suffix, match);

        var result = new System.Text.StringBuilder(after.Length);
        for (int j = 0; j < newLines.Count; j++)
        {
            var (text, eol) = newLines[j];
            result.Append(text);
            if (eol.Length == 0)
            {
                continue;
            }

            var originalEol = match[j] >= 0 ? oldLines[match[j]].Eol : string.Empty;
            result.Append(originalEol.Length > 0 ? originalEol : dominantEol);
        }

        return result.ToString();
    }

    // Splits into (text, terminator) pairs, recognizing CRLF, LF and lone CR. A final line without a
    // terminator gets an empty Eol.
    private static List<(string Text, string Eol)> SplitLinesWithEol(string content)
    {
        var lines = new List<(string Text, string Eol)>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\n')
            {
                lines.Add((content[start..i], "\n"));
                start = i + 1;
            }
            else if (c == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    lines.Add((content[start..i], "\r\n"));
                    i++;
                }
                else
                {
                    lines.Add((content[start..i], "\r"));
                }

                start = i + 1;
            }
        }

        if (start < content.Length)
        {
            lines.Add((content[start..], string.Empty));
        }

        return lines;
    }

    // Fills match[] for the differing middle (old [oldStart, oldEnd) vs new [newStart, newEnd)) with a
    // longest-common-subsequence over line text. match[newIndex] = oldIndex for matched lines, untouched otherwise.
    // A middle whose LCS table would exceed ~4M cells is treated as entirely changed (its lines take the dominant
    // EOL), which over-normalizes rather than failing or allocating without bound.
    private static void MatchMiddle(
        List<(string Text, string Eol)> oldLines, int oldStart, int oldEnd,
        List<(string Text, string Eol)> newLines, int newStart, int newEnd,
        int[] match)
    {
        const long maxCells = 4_000_000;
        int n = oldEnd - oldStart;
        int m = newEnd - newStart;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > maxCells)
        {
            return;
        }

        // lcs[i, j] = LCS length of old[oldStart + i ..] and new[newStart + j ..].
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[oldStart + i].Text == newLines[newStart + j].Text
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int a = 0;
        int b = 0;
        while (a < n && b < m)
        {
            if (oldLines[oldStart + a].Text == newLines[newStart + b].Text)
            {
                match[newStart + b] = oldStart + a;
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                a++;
            }
            else
            {
                b++;
            }
        }
    }
}
