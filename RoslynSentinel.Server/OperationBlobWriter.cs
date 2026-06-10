using System.Text;
using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server;

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
            var fileName  = $"{toolName}_{timestamp}_{changeId}.json";
            var filePath  = Path.Combine(dir, fileName);

            var payload = new
            {
                toolName,
                changeId,
                generatedUtc = DateTime.UtcNow.ToString("O"),
                itemCount    = items.Count,
                items,
            };

            await File.WriteAllTextAsync(
                filePath,
                JsonSerializer.Serialize(payload, PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return fileName;
        }
        catch (Exception ex)
        {
            return $"(blob write failed: {ex.Message})";
        }
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
