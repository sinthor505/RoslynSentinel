using System.Linq;
using System.Text;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Writes forensic operation blobs to .roslynsentinel/operations/ under the solution root.
/// Bypasses DocPathGuard and the agent-facing write rate limit — the filename is
/// server-controlled (trusted code, not agent-supplied input), so those guards do not apply.
/// </summary>
public static class OperationBlobWriter
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Writes a forensic blob for a batch operation and returns the blob filename on success.
    /// Returns a diagnostic string (not thrown) when the blob cannot be written.
    /// </summary>
    public static async Task<string> WriteAsync(
        string toolName,
        string changeId,
        List<OperationItemRecord> items,
        string? solutionRoot)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return "(no solution root — blob not written)";
        }

        try
        {
            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            Directory.CreateDirectory(dir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var fileName = $"{toolName}_{timestamp}_{changeId}.json";
            var filePath = Path.Combine(dir, fileName);

            var payload = new
            {
                toolName,
                changeId,
                generatedUtc = DateTime.UtcNow.ToString("O"),
                itemCount = items.Count,
                items,
            };

            await File.WriteAllTextAsync(
                filePath,
                JsonSerializer.Serialize(payload, PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Blob write failed: file not found after write operation.", filePath);
            }

            return fileName;
        }
        catch (Exception ex)
        {
            return $"(blob write failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Writes a compact validation-failure blob under .roslynsentinel/validation/.
    /// Called automatically by ValidationEngine when newly-introduced compiler errors are found.
    /// Returns the blob filename on success, or a diagnostic string on failure (never throws).
    /// </summary>
    public static async Task<string> WriteValidationFailureAsync(
        IEnumerable<string> changedFilePaths,
        List<DiagnosticInfo> diagnostics,
        string? solutionRoot)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return "(no solution root — validation blob not written)";
        }

        try
        {
            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "validation");
            Directory.CreateDirectory(dir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var changeId = Guid.NewGuid().ToString("N")[..8];
            var fileName = $"validation_{timestamp}_{changeId}.json";
            var filePath = Path.Combine(dir, fileName);

            var payload = new
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                changedFiles = changedFilePaths.ToList(),
                errorCount = diagnostics.Count,
                diagnostics,
            };

            await File.WriteAllTextAsync(
                filePath,
                JsonSerializer.Serialize(payload, PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return fileName;
        }
        catch (Exception ex)
        {
            return $"(validation blob write failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Writes a forensic blob for a completed write-through apply (direct-apply refactoring
    /// tools that no longer go through the staging dictionaries). Mirrors the per-changeId blob
    /// shape used by staged-change applies so UndoLastApply/GetOperationDetail resolve it
    /// identically. No-ops (returns a diagnostic string, never throws) when nothing was written.
    /// </summary>
    public static async Task<string> WriteApplyBlobAsync(
        string toolName,
        string changeId,
        PersistentWorkspaceManager.ApplyChangesResult result,
        string? solutionRoot)
    {
        if (result.SucceededFiles.Count == 0)
        {
            return "(no files written — blob not needed)";
        }

        var items = result.SucceededFiles.Select(f =>
        {
            string? before = null;
            result.PreImages?.TryGetValue(f, out before);
            return new OperationItemRecord
            {
                FilePath = f,
                Outcome = ItemRecordOutcome.Succeeded,
                BeforeSource = before,
            };
        }).ToList();

        return await WriteAsync(toolName, changeId, items, solutionRoot);
    }

    /// <summary>
    /// Locates the on-disk blob path for the given changeId, or null if not found.
    /// Blob filename pattern: {toolName}_{timestamp}_{changeId}.json
    /// </summary>
    public static string? FindBlobPath(string changeId, string? solutionRoot)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return null;
        }

        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, $"*_{changeId}.json").FirstOrDefault();
    }
}
