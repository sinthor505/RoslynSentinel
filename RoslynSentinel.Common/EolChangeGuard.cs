namespace RoslynSentinel.Common;

/// <summary>Counts of each physical line-ending style in a piece of text.</summary>
/// <param name="Crlf">Number of CR+LF pairs.</param>
/// <param name="Lf">Number of lone LF characters.</param>
/// <param name="Cr">Number of lone CR characters.</param>
public readonly record struct EolCounts(int Crlf, int Lf, int Cr)
{
    public int Total => Crlf + Lf + Cr;

    /// <summary>True when exactly one style occurs (and at least one line break exists).</summary>
    public bool IsSingleStyle => (Crlf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) == 1;

    /// <summary>The most frequent style; ties break CRLF, then LF, then CR. Empty when there are no line breaks.</summary>
    public string Dominant
    {
        get
        {
            if (Total == 0)
            {
                return "none";
            }

            if (Crlf >= Lf && Crlf >= Cr)
            {
                return "CRLF";
            }

            return Lf >= Cr ? "LF" : "CR";
        }
    }

    /// <summary>Human-readable style, e.g. "CRLF" or "mixed (CRLF x 40, LF x 2)".</summary>
    public string Describe()
    {
        if (Total == 0)
        {
            return "no line breaks";
        }

        if (IsSingleStyle)
        {
            return Dominant;
        }

        var parts = new List<string>();
        if (Crlf > 0)
        {
            parts.Add($"CRLF x {Crlf}");
        }

        if (Lf > 0)
        {
            parts.Add($"LF x {Lf}");
        }

        if (Cr > 0)
        {
            parts.Add($"CR x {Cr}");
        }

        return $"mixed ({string.Join(", ", parts)})";
    }
}

/// <summary>One file whose proposed content would change its line-ending style.</summary>
public record EolChangeViolation(string FilePath, EolCounts Before, EolCounts After)
{
    public string BeforeStyle => Before.Describe();

    public string AfterStyle => After.Describe();
}

/// <summary>
/// Write-path guardrail: refuses a change to an EXISTING C# file that alters that file's line-ending
/// style. A refactoring should only touch the lines it edits, so a changed EOL style is the
/// fingerprint of a whole-root normalize/reformat (the MoveMember bug that re-serialized entire
/// files through NormalizeWhitespace) or of new text spliced in with a different EOL than its
/// surroundings. No tool converts EOLs on purpose: every writer normalizes new text to the
/// file's existing dominant EOL (see <see cref="EolUtilities.NormalizeEol"/>).
/// </summary>
public static class EolChangeGuard
{
    public static EolCounts Count(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        return new EolCounts(crlf, lf, cr);
    }

    /// <summary>
    /// Returns a violation when <paramref name="after"/> changes the EOL style of
    /// <paramref name="before"/>, or null when it is acceptable. Rules: a null before (new file) is
    /// exempt; so is a before or after with no line breaks at all (no style to compare); a file that
    /// had a single style must still be exactly that style afterwards (this catches CRLF to LF and
    /// single to mixed); a file that was already mixed must keep the same dominant style (it stays
    /// mixed - never a violation by itself). Only .cs files are checked.
    /// </summary>
    public static EolChangeViolation? Check(string filePath, string? before, string after)
    {
        if (before is null || !string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var b = Count(before);
        var a = Count(after);
        if (b.Total == 0 || a.Total == 0)
        {
            return null;
        }

        var violated = b.IsSingleStyle
            ? !(a.IsSingleStyle && a.Dominant == b.Dominant)
            : a.Dominant != b.Dominant;
        return violated ? new EolChangeViolation(filePath, b, a) : null;
    }

    /// <summary>Checks every change against its pre-image; returns the violations (empty when none).</summary>
    public static List<EolChangeViolation> CheckAll(
        IEnumerable<KeyValuePair<FilePathWrapper, string>> changes,
        Func<string, string?> beforeLookup)
    {
        var violations = new List<EolChangeViolation>();
        foreach (var (path, after) in changes)
        {
            var violation = Check(path, beforeLookup(path), after);
            if (violation != null)
            {
                violations.Add(violation);
            }
        }

        return violations;
    }

    /// <summary>Actionable message: names each file, before and after styles, the likely cause, and the outcome.</summary>
    public static string BuildMessage(string operationName, IReadOnlyList<EolChangeViolation> violations)
    {
        var files = string.Join("; ", violations.Select(v =>
            $"'{v.FilePath}' (before: {v.BeforeStyle}, after: {v.AfterStyle})"));
        return $"{operationName}: refused - the change would alter the line endings of {violations.Count} existing file(s): {files}. " +
               "No files were written. A change must only touch the lines it edits, so this almost always means the producing " +
               "tool re-serialized the whole file (a whole-root NormalizeWhitespace/Format) or spliced in text with a different " +
               "EOL than the file uses. Re-issue the edit with a narrower, text-level change that keeps the file's existing line endings.";
    }
}
