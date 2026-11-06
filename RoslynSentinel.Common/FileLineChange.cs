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
    // Ceiling on the line edit distance (removed + added) that is still counted exactly. The exact count costs
    // O((N + M) * D) time and O(D) memory, so this bounds the work (~D^2/2 diagonal steps plus the matching runs).
    private const int MaxEditDistance = 8_000;

    /// <summary>
    /// Counts added/removed lines between <paramref name="before"/> and <paramref name="after"/>
    /// (null before = new file, so every line is added). Lines are compared INCLUDING their line
    /// terminator, so a CRLF to LF rewrite or a lost final newline shows up as changed lines instead
    /// of being invisible. The count is the minimal line edit (the diff behind a longest common
    /// subsequence), computed with Myers' O(ND) algorithm over the differing middle (common
    /// prefix/suffix lines are trimmed first), so it is exact for sparse edits at any file size: a
    /// few changed lines in a 10,000 line file report a few lines. Only when the edit distance
    /// (removed + added) exceeds <see cref="MaxEditDistance"/> is the whole middle counted as removed
    /// and re-added, which over-reports rather than hides a large rewrite.
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

        int editDistance = EditDistance(oldMiddle, newMiddle, MaxEditDistance);
        if (editDistance < 0)
        {
            // Too different to count exactly within the work bound: over-report the whole middle.
            return new FileLineChange(filePath, newMiddle.Length, oldMiddle.Length);
        }

        // removed + added = D and 2 * common = N + M - D, so the matched line count follows from D alone.
        int common = (oldMiddle.Length + newMiddle.Length - editDistance) / 2;
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

    /// <summary>
    /// Minimal edit distance (lines removed + lines added) between two line sequences, by the forward pass of
    /// Myers' O(ND) diff: each round d extends, on every diagonal k = x - y, the furthest point reachable with d
    /// edits, sliding along runs of equal lines for free. Only the distance is needed, so no trace is kept and
    /// memory is O(D). Returns -1 when the distance exceeds <paramref name="maxDistance"/>.
    /// </summary>
    private static int EditDistance(string[] oldLines, string[] newLines, int maxDistance)
    {
        int n = oldLines.Length;
        int m = newLines.Length;
        if (n == 0 || m == 0)
        {
            return n + m <= maxDistance ? n + m : -1;
        }

        // Intern each distinct line to an int once, so the inner loop compares ints rather than strings.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = Intern(oldLines, ids);
        var b = Intern(newLines, ids);

        int max = Math.Min(n + m, maxDistance);
        int offset = max + 1;
        var furthestX = new int[2 * max + 3];
        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && furthestX[offset + k - 1] < furthestX[offset + k + 1])
                    ? furthestX[offset + k + 1]
                    : furthestX[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                furthestX[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return d;
                }
            }
        }

        return -1;
    }

    private static int[] Intern(string[] lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (!ids.TryGetValue(lines[i], out int id))
            {
                id = ids.Count;
                ids[lines[i]] = id;
            }

            result[i] = id;
        }

        return result;
    }
}
