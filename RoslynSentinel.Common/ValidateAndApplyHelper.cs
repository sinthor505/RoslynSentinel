using Microsoft.Extensions.Logging;

using ModelContextProtocol;

namespace RoslynSentinel.Common;

/// <summary>
/// Shared implementation of the validate-then-write-through pattern used by both
/// SentinelRefactoringTools (Basic) and SentinelAdvancedRefactoringTools (Advanced) — previously
/// duplicated verbatim in each. Validates proposed changes against the current in-memory
/// solution and, unless <paramref name="dryRun"/> is set, writes them straight to disk via
/// <see cref="IWorkspaceManager.ApplyProposedChangesAsync"/> (write-through — no
/// intermediate staging step). Rolls back any already-written files if a multi-file change
/// partially fails, so a change never lands half-applied.
/// </summary>
public static class ValidateAndApplyHelper
{
    public static async Task<ApplyOutcome> ValidateAndApplyAsync(
        ValidationEngine validationEngine,
        IWorkspaceManager workspaceManager,
        ILogger logger,
        Dictionary<FilePathWrapper, string> changes,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null,
        Func<DiagnosticReport, CancellationToken, Task<string>>? describeValidationFailure = null)
    {
        DiagnosticReport validation;
        try
        {
            // Files actually being deleted from disk also need their Document dropped from the
            // candidate solution before compiling — same reasoning as removePaths (a rename's old
            // path), just via a different route (an on-disk delete instead of a superseding write).
            var allRemovePaths = deletePaths == null
                ? removePaths
                : (removePaths ?? []).Concat(deletePaths).ToList();
            validation = await validationEngine.ValidateChangesAsync(changes, allRemovePaths, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ValidateAndApply pre-validate failed for {OperationName}", operationName);
            return new ApplyOutcome(null, ToolErrorMapper.ToResultError(ex, workspaceManager, $"{operationName} pre-validate"), dryRun);
        }

        if (!validation.Success)
        {
            var detail = describeValidationFailure != null
                ? await describeValidationFailure(validation, cancellationToken)
                : validation.Diagnostics.ToJson();
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName}: the change was valid and matched its target(s), but introduces new compiler errors — change not applied. " +
                $"Fix the issue(s) below and retry:\n{detail}"), dryRun);
        }

        if (dryRun)
        {
            var previewDiff = returnDiff ? await BuildDiffAsync(workspaceManager, changes, cancellationToken) : null;
            return new ApplyOutcome(null, null, true, previewDiff);
        }

        var applyResult = await workspaceManager.ApplyProposedChangesAsync(
            changes, retryCount: 3, validateChanges: false, rollbackOnPartialFailure: true,
            progress: progress, cancellationToken: cancellationToken, deletePaths: deletePaths);

        if (!applyResult.Success)
        {
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} apply failed: {applyResult.Summary}"), false);
        }

        var changeId = Guid.NewGuid().ToString("n")[..8];
        var blob = await OperationBlobWriter.WriteApplyBlobAsync(
            operationName, changeId, applyResult, workspaceManager.GetSolutionRoot(), logger);

        var appliedDiff = returnDiff ? BuildDiffFromPreImages(changes, applyResult.PreImages) : null;

        // The blob-integrity invariant, enforced where both facts are known at once: an apply that
        // wrote files and issues a changeId must have a resolvable blob. This return value used to
        // be discarded entirely, which is why run 20260910-013550-398 saw status:"applied" with a
        // confident undo instruction and no blob on disk.
        //
        // Deliberately not thrown: the files are already written, and reporting a landed edit as
        // failed would invite the model to retry it — a corruption path worse than a missing undo
        // record. Instead the result tells the truth (applied, not reversible) and the breaker
        // refuses every subsequent mutation.
        if (blob.IsIntegrityFailure)
        {
            var reason = blob.Diagnostic ?? "the operation blob could not be written";
            ((IUnrecoverableBreaker)workspaceManager).Trip(operationName, changeId, reason);

            // No changeId: one UndoLastApply cannot resolve is worse than none.
            return new ApplyOutcome(null, null, false, appliedDiff, reason);
        }

        // Nothing was written, so no changeId should be issued either. Previously one was minted
        // unconditionally, which meant any operation producing an empty change set — most commonly
        // one whose refactoring feature is disabled in SentinelConfiguration, e.g. ExtractInterface,
        // which returns an empty dictionary rather than an error — reported status:"applied" and
        // handed back a handle UndoLastApply could never resolve. Not an integrity failure (nothing
        // landed on disk), just a no-op, so the breaker deliberately stays untripped.
        //
        // Keyed on the apply result rather than on blob.Required: those diverge when there is no
        // solution root at all (in-memory test solutions), where files are written to the workspace
        // but no blob is applicable. Withholding the changeId there would misreport a real change.
        if (applyResult.SucceededFiles.Count == 0)
        {
            return new ApplyOutcome(null, null, false, appliedDiff,
                $"{operationName} produced no file changes, so nothing was written and there is " +
                "nothing to undo. If you expected a change, the operation matched no target — or " +
                "its refactoring feature is disabled on this server (see the Features tool).");
        }

        return new ApplyOutcome(changeId, null, false, appliedDiff);
    }

    public static async Task<string> BuildDiffAsync(
        IWorkspaceManager workspaceManager,
        Dictionary<FilePathWrapper, string> changes,
        CancellationToken cancellationToken)
    {
        var solution = await workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var parts = new List<string>();
        foreach (var (path, newText) in changes)
        {
            var docId = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            string before = "";
            if (docId != null)
            {
                var doc = solution.GetDocument(docId);
                before = (await doc!.GetTextAsync(cancellationToken)).ToString();
            }
            else if (File.Exists(path))
            {
                before = await File.ReadAllTextAsync(path, cancellationToken);
            }
            parts.Add($"--- {path}\n{DiffEngine.CreateDiff(before, newText)}");
        }
        return string.Join("\n", parts);
    }

    public static string BuildDiffFromPreImages(
        Dictionary<FilePathWrapper, string> changes,
        IReadOnlyDictionary<string, string?>? preImages)
    {
        var parts = new List<string>();
        foreach (var (path, newText) in changes)
        {
            string before = preImages != null && preImages.TryGetValue(path, out var pre) && pre != null ? pre : "";
            parts.Add($"--- {path}\n{DiffEngine.CreateDiff(before, newText)}");
        }
        return string.Join("\n", parts);
    }
}
