using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Shared static helper for writing the forensic operation blob that undo_last_apply relies on.
/// Extracted verbatim from SentinelWorkspaceTools.WriteBlobForApplyAsync (see
/// docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md, Decision 7 step 1) so both
/// the file-edit and project-management tool clusters can call it without a cross-class dependency.
/// </summary>
public static class OperationBlobHelper
{
    /// <summary>
    /// Writes a forensic blob for a completed apply so undo_last_apply can revert it.
    /// Uses pre-images from ApplyChangesResult.PreImages (populated by ApplyProposedChangesAsync).
    /// blobChangeId: if provided, uses this id for the blob filename; if null, mints a fresh id.
    /// </summary>
    /// <remarks>
    /// Never throws: the apply already succeeded and the files are on disk, so failing the call
    /// would report a landed edit as failed and invite a retry. On failure it instead trips the
    /// unrecoverable breaker, which refuses every subsequent mutating call this session -> the
    /// previous behaviour was a log-only warning that no caller and no transcript ever saw.
    /// Returns the result so callers can suppress their own undo advice; the trip happens here
    /// regardless, so a caller that ignores it still cannot proceed.
    /// </remarks>
    public static async Task<BlobWriteResult> WriteBlobForApplyAsync(ILogger logger, IWorkspaceManager workspaceManager,
        string toolName, ApplyChangesResult result, string? blobChangeId = null,
        CancellationToken cancellationToken = default)
    {
        if (result.SucceededFiles.Count == 0)
        {
            return BlobWriteResult.NotNeeded("no files written - blob not needed");
        }

        var changeId = blobChangeId ?? Guid.NewGuid().ToString("n")[..8];
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
        var blob = await OperationBlobWriter.WriteAsync(
            toolName, changeId, items, workspaceManager.GetSolutionRoot(), logger, cancellationToken);

        if (blob.IsIntegrityFailure)
        {
            // Files landed with no undo record. OperationBlobWriter has already logged the
            // exception at Error; the trip is what makes it consequential rather than advisory.
            ((IUnrecoverableBreaker)workspaceManager).Trip(
                toolName, changeId, blob.Diagnostic ?? "the operation blob could not be written");
        }
        else if (blob.Written && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Forensic blob written: {BlobName} (changeId={ChangeId})", blob.FileName, changeId);
        }

        return blob;
    }
}
