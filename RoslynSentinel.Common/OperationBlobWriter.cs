using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Outcome of an operation-blob write. Three-way rather than a bool: "not needed" (nothing was
/// written, so no undo record is owed) must not be confused with "failed" (something was written
/// and is now unreversible), which is a server-integrity fault.
/// </summary>
/// <param name="Written">True only when a blob exists on disk for this changeId.</param>
/// <param name="FileName">The blob's filename when <paramref name="Written"/>; otherwise null.</param>
/// <param name="Diagnostic">Why no blob was written. Null when one was.</param>
/// <param name="Required">
/// True when a blob was owed — i.e. files were written. False for the no-op case, which lets
/// callers treat only <c>Required &amp;&amp; !Written</c> as a fault.
/// </param>
public sealed record BlobWriteResult(bool Written, string? FileName, string? Diagnostic, bool Required)
{
    public static BlobWriteResult Success(string fileName) => new(true, fileName, null, true);

    public static BlobWriteResult Failed(string diagnostic) => new(false, null, diagnostic, true);

    public static BlobWriteResult NotNeeded(string diagnostic) => new(false, null, diagnostic, false);

    /// <summary>True when a blob was owed but is absent — the change applied but cannot be undone.</summary>
    public bool IsIntegrityFailure => Required && !Written;
}

/// <summary>
/// Writes forensic operation blobs to .roslynsentinel/operations/ under the solution root.
/// Bypasses DocPathGuard and the agent-facing write rate limit — the filename is
/// server-controlled (trusted code, not agent-supplied input), so those guards do not apply.
/// </summary>
public static class OperationBlobWriter
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Characters that must not reach a blob filename. <see cref="Path.GetInvalidFileNameChars"/>
    /// omits the directory separators on some platforms, so both are added explicitly — a slashed
    /// operation name was the root cause of the A3 defect (see <see cref="SanitizeToolName"/>).
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
        [.. Path.GetInvalidFileNameChars(), '/', '\\'];

    /// <summary>
    /// Replaces anything unusable in a filename with '_'.
    /// </summary>
    /// <remarks>
    /// Sanitizing here rather than trusting callers is the durable fix. Seventeen call sites in
    /// SentinelAdvancedRefactoringTools passed slashed operation names ("WrapRange/region",
    /// "Inline/method", …); <see cref="Path.Combine"/> then resolved the blob into a
    /// non-existent operations/WrapRange/ subdirectory, the write threw
    /// DirectoryNotFoundException, and the catch below swallowed it into a return string nobody
    /// inspected. Five tools returned changeIds that UndoLastApply could never resolve. The call
    /// sites were renamed too, but this holds for any future name regardless of caller discipline.
    /// </remarks>
    private static string SanitizeToolName(string toolName)
    {
        var sanitized = toolName;
        foreach (var invalid in InvalidFileNameChars)
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    /// <summary>
    /// Writes a forensic blob for a batch operation.
    /// </summary>
    /// <remarks>
    /// Returns a typed <see cref="BlobWriteResult"/> rather than the old "filename, or a string
    /// starting with '(' on failure" convention: that convention made failure easy to ignore (see
    /// ValidateAndApplyHelper, which discarded the value entirely) and was itself a latent bug,
    /// since any future filename beginning with '(' would have read as a failure.
    /// </remarks>
    public static async Task<BlobWriteResult> WriteAsync(
        string toolName,
        string changeId,
        List<OperationItemRecord> items,
        string? solutionRoot,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            // NotNeeded, not Failed: with no solution root there is nowhere a blob could live and
            // no undo semantics to speak of — this is the in-memory/test-solution case, not a
            // server fault. Classifying it as a failure tripped the unrecoverable breaker on the
            // first apply of every SetTestSolution-based fixture and refused all subsequent ones.
            return BlobWriteResult.NotNeeded("no solution root — blob not applicable");
        }

        try
        {
            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            Directory.CreateDirectory(dir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var fileName = $"{SanitizeToolName(toolName)}_{timestamp}_{changeId}.json";
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
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Blob write failed: file not found after write operation.", filePath);
            }

            return BlobWriteResult.Success(fileName);
        }
        catch (Exception ex)
        {
            // Logged at Error with the exception attached: the previous catch discarded it
            // entirely, which is why the DirectoryNotFoundException behind the A3 defect never
            // appeared anywhere at all.
            logger?.LogError(ex,
                "Operation blob write failed for {ToolName}/{ChangeId} — the change is not reversible via UndoLastApply.",
                toolName, changeId);
            return BlobWriteResult.Failed($"blob write failed: {ex.GetType().Name}: {ex.Message}");
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
    public static async Task<BlobWriteResult> WriteApplyBlobAsync(
        string toolName,
        string changeId,
        ApplyChangesResult result,
        string? solutionRoot,
        ILogger? logger = null)
    {
        if (result.SucceededFiles.Count == 0)
        {
            // Not a failure: nothing was written, so there is nothing to reverse and no blob is
            // owed. Distinguished from a real write failure by NotNeeded, so callers don't warn.
            return BlobWriteResult.NotNeeded("no files written — blob not needed");
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

        return await WriteAsync(toolName, changeId, items, solutionRoot, logger);
    }

    /// <summary>
    /// Writes a batch-operation blob and enforces the blob-integrity invariant: an operation that
    /// wrote files and returns a changeId must have a resolvable blob. Returns the value for
    /// <c>BatchResultSummary.BlobName</c>.
    /// </summary>
    /// <remarks>
    /// For the batch tools (Asyncify, BulkComment, …), whose result shape carries a
    /// <c>BlobName</c> string rather than an <see cref="ApplyOutcome"/>. Those ten call sites
    /// previously assigned <see cref="WriteAsync"/>'s return value straight into that field and
    /// never checked it, so a failed write surfaced to the agent as a plausible-looking blob name
    /// beginning with '(' alongside a changeId <c>UndoLastApply</c> could not resolve. Centralized
    /// so the trip cannot be forgotten at a call site — the omission mode this whole change fixes.
    /// The <see cref="ValidateAndApplyHelper"/> path does the equivalent for the write-through
    /// refactoring tools.
    /// </remarks>
    public static async Task<string> WriteBatchBlobOrTripAsync(
        IUnrecoverableBreaker breaker,
        string toolName,
        string changeId,
        List<OperationItemRecord> items,
        string? solutionRoot,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var blob = await WriteAsync(toolName, changeId, items, solutionRoot, logger, cancellationToken);

        if (blob.IsIntegrityFailure)
        {
            breaker.Trip(toolName, changeId, blob.Diagnostic ?? "the operation blob could not be written");
        }

        // Reported to the agent verbatim either way: on failure the diagnostic is more useful in
        // the result than a bare empty string, and the halt is what actually stops the session.
        return blob.FileName ?? $"({blob.Diagnostic})";
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
