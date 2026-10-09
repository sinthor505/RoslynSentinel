namespace RoslynSentinel.Common;

/// <summary>Helper methods for generating human-readable messages about external file drift.</summary>
public static class DriftMessages
{
    /// <summary>
    /// Summarizes file paths as a comma-separated list of distinct file names, with an overflow suffix.
    /// </summary>
    /// <param name="paths">Collection of file paths to summarize.</param>
    /// <param name="max">Maximum number of distinct file names to show before adding a "+N more" suffix (default 5).</param>
    /// <returns>A human-readable summary, or "no files" if the collection is empty.</returns>
    public static string SummarizeFiles(IReadOnlyCollection<string> paths, int max = 5)
    {
        if (paths.Count == 0)
            return "no files";

        // Extract file names, deduplicate with case-insensitive comparison, keep first-seen order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctNames = new List<string>();

        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            if (seen.Add(fileName))
                distinctNames.Add(fileName);
        }

        if (distinctNames.Count <= max)
            return string.Join(", ", distinctNames);

        var shown = distinctNames.Take(max);
        var remaining = distinctNames.Count - max;
        return $"{string.Join(", ", shown)} (+{remaining} more)";
    }

    /// <summary>
    /// Builds a diagnostic hint message when external file changes are detected, or returns null if none.
    /// </summary>
    /// <param name="externalChanges">Collection of file paths that changed outside the server.</param>
    /// <returns>A formatted hint message, or null if the collection is empty.</returns>
    public static string? BuildHint(IReadOnlyCollection<string> externalChanges)
    {
        if (externalChanges.Count == 0)
            return null;

        var count = externalChanges.Count;
        var summary = SummarizeFiles(externalChanges);
        return $"{count} file(s) changed on disk outside this server ({summary}). " +
               "If these errors do not match your edits, run ListExternalDiskChanges and " +
               "Git(operation: status) before changing code.";
    }

    /// <summary>
    /// Resolves a list of requested selection tokens against a current list of available entries,
    /// matching by case-insensitive equality or suffix after normalizing path separators.
    /// </summary>
    /// <param name="current">List of available entries to match against.</param>
    /// <param name="requested">List of selection tokens (trimmed, blank entries skipped).</param>
    /// <param name="matched">Output list of distinct, matched current entries in first-seen order.</param>
    /// <param name="unmatched">Output list of requested tokens that did not match anything.</param>
    public static void ResolveSelection(IReadOnlyList<string> current, IReadOnlyList<string> requested,
        out List<string> matched, out List<string> unmatched)
    {
        matched = new List<string>();
        unmatched = new List<string>();

        var matchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in requested)
        {
            var trimmed = token.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var normalizedToken = trimmed.Replace('/', '\\');
            var found = false;

            foreach (var entry in current)
            {
                var normalizedEntry = entry.Replace('/', '\\');

                // Match by exact equality (case-insensitive) or by suffix after backslash
                if (string.Equals(normalizedEntry, normalizedToken, StringComparison.OrdinalIgnoreCase) ||
                    normalizedEntry.EndsWith("\\" + normalizedToken, StringComparison.OrdinalIgnoreCase))
                {
                    if (matchedSet.Add(entry))
                        matched.Add(entry);

                    found = true;
                }
            }

            if (!found)
                unmatchedSet.Add(trimmed);
        }

        // Add unmatched tokens in order, distinct
        foreach (var token in requested)
        {
            var trimmed = token.Trim();
            if (!string.IsNullOrEmpty(trimmed) && unmatchedSet.Contains(trimmed))
                unmatched.Add(trimmed);
        }
    }
}
