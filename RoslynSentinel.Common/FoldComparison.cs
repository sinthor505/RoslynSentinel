namespace RoslynSentinel.Common;

/// <summary>Outcome of comparing an in-memory document's text with its disk text.</summary>
public enum FoldComparisonOutcome
{
    /// <summary>The two texts are identical (ordinal).</summary>
    Equal,

    /// <summary>The texts differ only by a leading BOM, line endings or per-line trailing whitespace.</summary>
    Converged,

    /// <summary>The texts differ in content.</summary>
    Mismatch,
}

/// <summary>
/// Decides whether an in-memory document still matches its disk text. Step 8b-2 in PersistentWorkspaceManager
/// consumes it: Converged callers proceed with an Info finding; Mismatch callers refuse the write.
/// </summary>
public static class FoldComparison
{
    /// <summary>
    /// Compares disk text with memory text: ordinal equality first, then equality after normalization (leading BOM
    /// stripped, EOLs to LF, per-line trailing whitespace trimmed). A final-newline difference stays a Mismatch.
    /// </summary>
    public static FoldComparisonOutcome Compare(string diskText, string memoryText)
    {
        if (string.Equals(diskText, memoryText, StringComparison.Ordinal))
        {
            return FoldComparisonOutcome.Equal;
        }

        return string.Equals(Normalize(diskText), Normalize(memoryText), StringComparison.Ordinal)
            ? FoldComparisonOutcome.Converged
            : FoldComparisonOutcome.Mismatch;
    }

    private static string Normalize(string text)
    {
        var withoutBom = text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        var lf = withoutBom.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = lf.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }

        return string.Join("\n", lines);
    }
}
