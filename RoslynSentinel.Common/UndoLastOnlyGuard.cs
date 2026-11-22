using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Last-only undo guard. An undo of an older change is refused while a newer operation blob still stands on one of the
/// same files, so the newer change is undone first. Scans every blob in the operations folder rather than a time window,
/// and skips blobs whose undo is already recorded. Never throws.
/// </summary>
public static class UndoLastOnlyGuard
{
    private const int MaxListedChanges = 5;

    private static readonly string[] NonStandingOutcomes = ["Skipped", "Failed", "RolledBack"];

    /// <summary>
    /// Returns a refusal message naming the newer changes that touched the same files as <paramref name="changeId"/>,
    /// or null when no newer change counts or nothing can be said.
    /// </summary>
    public static string? FindNewerWriters(string solutionRoot, string targetBlobPath, string changeId, IReadOnlyCollection<string> targetFilePaths)
    {
        try
        {
            return FindNewerWritersCore(solutionRoot, targetBlobPath, changeId, targetFilePaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? FindNewerWritersCore(string solutionRoot, string targetBlobPath, string changeId, IReadOnlyCollection<string> targetFilePaths)
    {
        if (string.IsNullOrEmpty(solutionRoot) || string.IsNullOrEmpty(targetBlobPath))
        {
            return null;
        }

        var targetTime = NotFoundEvidence.TimestampOf(targetBlobPath);
        if (targetTime is null)
        {
            return null;
        }

        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var targets = new HashSet<string>(
            targetFilePaths.Where(path => !string.IsNullOrEmpty(path)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
        {
            return null;
        }

        var targetName = Path.GetFileName(targetBlobPath);
        var matchedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newer = new List<(DateTime Written, string FileName, string ChangeId, string? ToolName, List<string> SharedFiles)>();

        foreach (var blobPath in Directory.EnumerateFiles(dir, "*.json"))
        {
            var fileName = Path.GetFileName(blobPath);
            if (string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var written = NotFoundEvidence.TimestampOf(blobPath);
            if (written is null || written.Value < targetTime.Value)
            {
                continue;
            }

            if (written.Value == targetTime.Value && string.CompareOrdinal(fileName, targetName) <= 0)
            {
                continue;
            }

            if (OperationBlobWriter.IsUndone(blobPath))
            {
                continue;
            }

            using var document = TryParse(blobPath);
            if (document is null
                || document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var shared = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var outcome = StringOf(item, "Outcome");
                var itemPath = StringOf(item, "FilePath");
                if (outcome is null || NonStandingOutcomes.Contains(outcome) || string.IsNullOrEmpty(itemPath))
                {
                    continue;
                }

                var key = NormalizePath(itemPath);
                if (!targets.Contains(key))
                {
                    continue;
                }

                matchedTargets.Add(key);
                var name = Path.GetFileName(itemPath);
                if (!shared.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    shared.Add(name);
                }
            }

            if (shared.Count == 0)
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            newer.Add((written.Value, fileName, stem[(stem.LastIndexOf('_') + 1)..], StringOf(document.RootElement, "toolName"), shared));
        }

        if (newer.Count == 0)
        {
            return null;
        }

        var ordered = newer
            .OrderByDescending(entry => entry.Written)
            .ThenByDescending(entry => entry.FileName, StringComparer.Ordinal)
            .ToList();

        var listed = string.Join(", ", ordered.Take(MaxListedChanges).Select(entry =>
            $"{entry.ChangeId} ({entry.ToolName ?? "unknown tool"}: {string.Join(", ", entry.SharedFiles)})"));
        var hidden = ordered.Count - MaxListedChanges;
        if (hidden > 0)
        {
            listed += $", and {hidden} more";
        }

        return $"ChangeId '{changeId}' is not the newest change for {matchedTargets.Count} of its file(s). "
            + $"Newer change(s) that touched the same file(s): {listed}. "
            + "Options: undo the newest of these changes first and work back to this one; edit the file(s) by hand; or revert with the Git tool.";
    }

    // A blob may be mid-write by another window, so an unreadable one is skipped rather than failing the scan.
    private static JsonDocument? TryParse(string blobPath)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(blobPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Compared case-insensitively with one separator style; a rooted path is made absolute first so that a relative and an absolute spelling of one file meet.
    private static string NormalizePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                // Fall through: compare the spelling as written.
            }
        }

        return path.Replace('\\', '/');
    }
}
