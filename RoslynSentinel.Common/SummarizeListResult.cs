namespace RoslynSentinel.Common;

public static class SummarizeListResult
{
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
    /// <summary>
    /// Like <see cref="Build{T}"/> but also fills <see cref="ListSummary.ByProject"/> from <paramref name="projectSelector"/>.
    /// </summary>
    public static ListSummary BuildWithProjects<T>(IReadOnlyCollection<T> items, Func<T, string> filePathSelector, Func<T, string> projectSelector, int maxFilesShown = 20)
    {
        var byProject = items
            .GroupBy(projectSelector)
            .Select(g => new ProjectHitCount(g.Key, g.Count()))
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Build(items, filePathSelector, maxFilesShown) with { ByProject = byProject };
    }
}
/// <summary>One file's hit count within a <see cref="ListSummary"/>.</summary>
public record FileHitCount(string FilePath, int Count);
/// <summary>One project's hit count within a <see cref="ListSummary"/>.</summary>
public record ProjectHitCount(string ProjectName, int Count);
/// <summary>
/// Per-file breakdown of a list-shaped tool result, built by <see cref="SummarizeListResult"/>.
/// <see cref="ByFile"/> is sorted by <see cref="FileHitCount.Count"/> descending and capped at the
/// caller's requested max; <see cref="TruncatedFileCount"/> is the number of additional files with
/// at least one hit that didn't fit in <see cref="ByFile"/>.
/// </summary>
public record ListSummary(int TotalCount, int FileCount, IReadOnlyList<FileHitCount> ByFile, int TruncatedFileCount)
{
    /// <summary>
    /// Optional per-project hit counts (descending by count, then name). Null unless the producing tool
    /// asked for it via <see cref="SummarizeListResult.BuildWithProjects{T}"/>; omitted from the JSON when null.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectHitCount>? ByProject { get; init; }

    /// <summary>
    /// Renders a single human-readable line of counts only, e.g. "12 hits across 5 files.". Meant for a
    /// tool's top-level StatusMessage so a caller sees the shape of the result (1 big hit vs. many small
    /// ones) without opening the payload. The per-file paths are deliberately not repeated here: they
    /// already travel in <see cref="ByFile"/> of the same envelope (listSummary.byFile), and repeating
    /// them only doubled the file paths in every list response.
    /// </summary>
    public string ToSummaryMessage(string itemNoun = "hit")
    {
        if (TotalCount == 0)
        {
            return $"0 {itemNoun}s.";
        }

        var itemPlural = itemNoun.EndsWith("ch", StringComparison.Ordinal) || itemNoun.EndsWith("sh", StringComparison.Ordinal) || itemNoun.EndsWith("s", StringComparison.Ordinal)
            ? itemNoun + "es"
            : itemNoun + "s";
        return $"{TotalCount} {(TotalCount == 1 ? itemNoun : itemPlural)} across {FileCount} file{(FileCount == 1 ? "" : "s")}.";
    }
}
