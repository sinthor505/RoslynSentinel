namespace RoslynSentinel.Common;

public static class SummarizeListResult
{
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Builds a per-file hit-count breakdown for a list-shaped tool result. Intended for any tool
    /// whose result items each carry a file path (FindReferences' CallerInfo/ImplementationInfo,
    /// SearchSolutionText's TextSearchMatch, etc.) so the tool can surface "1 big hit vs. 300 small
    /// ones" as a top-level hint instead of forcing the caller to open the payload (or, once
    /// offloaded, page through GetLargeResult) just to find out how big the result actually is.
    /// </summary>
    /// <param name="items">The result items to summarize.</param>
    /// <param name="filePathSelector">Extracts the file path from one item.</param>
    /// <param name="maxFilesShown">Caps how many distinct files appear in <see cref="ListSummary.ByFile"/>; the rest are folded into <see cref="ListSummary.TruncatedFileCount"/>.</param>
    public static ListSummary Build<T>(IReadOnlyCollection<T> items, Func<T, string> filePathSelector, int maxFilesShown = 20)
    {
        var byFile = items
            .GroupBy(filePathSelector)
            .Select(g => new FileHitCount(g.Key, g.Count()))
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = byFile.Take(maxFilesShown).ToList();
        return new ListSummary(items.Count, byFile.Count, shown, byFile.Count - shown.Count);
    }
}
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>One file's hit count within a <see cref="ListSummary"/>.</summary>
public record FileHitCount(string FilePath, int Count);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Per-file breakdown of a list-shaped tool result, built by <see cref="SummarizeListResult"/>.
/// <see cref="ByFile"/> is sorted by <see cref="FileHitCount.Count"/> descending and capped at the
/// caller's requested max; <see cref="TruncatedFileCount"/> is the number of additional files with
/// at least one hit that didn't fit in <see cref="ByFile"/>.
/// </summary>
public record ListSummary(int TotalCount, int FileCount, IReadOnlyList<FileHitCount> ByFile, int TruncatedFileCount)
{
    /// <summary>
    /// Renders a single human-readable line, e.g. "12 hits across 5 files: Foo.cs (4), Bar.cs (3),
    /// Baz.cs (2) (+2 more files)". Meant for a tool's top-level StatusMessage so a caller sees the
    /// shape of the result (1 big hit vs. many small ones) without opening the payload.
    /// </summary>
    public string ToStatusMessage(string itemNoun = "hit")
    {
        if (TotalCount == 0)
        {
            return $"0 {itemNoun}s.";
        }

        var shown = string.Join(", ", ByFile.Select(f => $"{f.FilePath} ({f.Count})"));
        var more = TruncatedFileCount > 0 ? $" (+{TruncatedFileCount} more file{(TruncatedFileCount == 1 ? "" : "s")})" : "";
        return $"{TotalCount} {itemNoun}{(TotalCount == 1 ? "" : "s")} across {FileCount} file{(FileCount == 1 ? "" : "s")}: {shown}{more}.";
    }
}
