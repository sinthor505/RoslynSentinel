namespace RoslynSentinel.Common;

/// <summary>
/// How many lines one file's write added and removed, computed from the text diff of its
/// pre-image against the written content. Reported so a caller (or reviewer) can see at a glance
/// whether an edit that should touch a handful of lines actually rewrote the whole file.
/// </summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="LinesAdded">Lines present after but not before.</param>
/// <param name="LinesRemoved">Lines present before but not after.</param>
public record FileLineChange(string FilePath, int LinesAdded, int LinesRemoved)
{
    private const long MaxLcsCells = 16_000_000;

    /// <summary>
    /// Counts added/removed lines between <paramref name="before"/> and <paramref name="after"/>
    /// (null before = new file, so every line is added). Lines are compared INCLUDING their line
    /// terminator, so a CRLF to LF rewrite or a lost final newline shows up as changed lines instead
    /// of being invisible. A pure LCS over the differing middle (common prefix/suffix lines are
    /// trimmed first); when that middle is too large for the LCS table the whole middle is counted
    /// as removed and re-added, which over-reports rather than hides a large rewrite.
    /// </summary>
    public static FileLineChange Compute(string filePath, string? before, string? after)
    {
        var oldLines = SplitLines(before ?? string.Empty);
        var newLines = SplitLines(after ?? string.Empty);

        int prefix = 0;
        int maxPrefix = Math.Min(oldLines.Length, newLines.Length);
        while (prefix < maxPrefix && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        int maxSuffix = maxPrefix - prefix;
        while (suffix < maxSuffix &&
               oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
        {
            suffix++;
        }

        var oldMiddle = oldLines[prefix..(oldLines.Length - suffix)];
        var newMiddle = newLines[prefix..(newLines.Length - suffix)];

        int common = (long)oldMiddle.Length * newMiddle.Length <= MaxLcsCells
            ? LcsLength(oldMiddle, newMiddle)
            : 0;

        return new FileLineChange(filePath, newMiddle.Length - common, oldMiddle.Length - common);
    }

    /// <summary>Splits into lines that keep their terminating '\n' (and any preceding '\r').</summary>
    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text.Substring(start));
        }

        return lines.ToArray();
    }

    private static int LcsLength(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                current[j] = a[i] == b[j]
                    ? previous[j + 1] + 1
                    : Math.Max(previous[j], current[j + 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[0];
    }
}
