using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Shared implementation of the validate-then-write-through pattern used by both
/// RefactoringStructuralTools (Basic) and AdvancedRefactoringTools (Advanced) -> previously
/// duplicated verbatim in each. Validates proposed changes against the current in-memory
/// solution and, unless <paramref name="dryRun"/> is set, writes them straight to disk via
/// <see cref="IWorkspaceManager.ApplyProposedChangesAsync"/> (write-through -> no
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
        IProgress<EngineProgress>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null,
        Func<DiagnosticReport, CancellationToken, Task<string>>? describeValidationFailure = null,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? expectedEdits = null)
    {
        // The pre-images feed the per-file changed-line counts and the collateral-change tripwire below.
        // EOL conversion is done by the chokepoint (PersistentWorkspaceManager.ApplyProposedChangesAsync).
        Dictionary<string, string?> beforeTexts;
        try
        {
            beforeTexts = await ReadBeforeTextsAsync(
                workspaceManager, changes.Keys.Select(k => (string)k).Concat((deletePaths ?? []).Select(k => (string)k)), cancellationToken);
        }
        // TODO cancellation: ToolErrorMapper maps OCE to a Cancelled result; decide rethrow vs result; see docs/current/proposals/proposal_throw_typed_exceptions_not_failure_results.md S1a
        catch (Exception ex)
        {
            logger.LogError(ex, "ValidateAndApply pre-image read failed for {OperationName}", operationName);
            return new ApplyOutcome(null, ToolErrorMapper.ToResultError(ex, workspaceManager, $"{operationName} pre-image read"), dryRun);
        }

        // Write-path tripwire for post-edit reformatting, run before the compile gate so a dry run
        // reports it too: when a caller passes expectedEdits, the edits it proposed must explain the
        // proposed text of each existing file. A mismatch means text the edits did not touch was
        // rewritten (see docs/current/plans/plan_movemember_collateral_change_tripwire.md).
        if (expectedEdits != null)
        {
            var collateralViolations = new List<CollateralChangeViolation>();
            foreach (var (key, edits) in expectedEdits)
            {
                if (!changes.TryGetValue(key, out var proposedText))
                {
                    continue;
                }

                var preImage = beforeTexts.GetValueOrDefault((string)key);
                if (preImage == null)
                {
                    continue;
                }

                var violation = CollateralChangeGuard.Check((string)key, preImage, proposedText, edits);
                if (violation != null)
                {
                    collateralViolations.Add(violation);
                }
            }

            if (collateralViolations.Count > 0)
            {
                logger.LogWarning("{OperationName} refused: edits do not explain the proposed text of {Count} file(s)", operationName, collateralViolations.Count);
                return new ApplyOutcome(null, new ResultError(
                    ToolErrorCode.CollateralChangeRefused,
                    CollateralChangeGuard.BuildMessage(operationName, collateralViolations),
                    StructuredDetail: collateralViolations.Cast<object>().ToList()), dryRun);
            }
        }

        IReadOnlyCollection<FilePathWrapper>? allRemovePaths;
        DiagnosticReport validation;
        try
        {
            // Files actually being deleted from disk also need their Document dropped from the
            // candidate solution before compiling -> same reasoning as removePaths (a rename's old
            // path), just via a different route (an on-disk delete instead of a superseding write).
            allRemovePaths = deletePaths == null
                ? removePaths
                : (removePaths ?? []).Concat(deletePaths).ToList();
            validation = await validationEngine.ValidateChangesAsync(changes, allRemovePaths, cancellationToken);
        }
        // TODO cancellation: ToolErrorMapper maps OCE to a Cancelled result; decide rethrow vs result; see docs/current/proposals/proposal_throw_typed_exceptions_not_failure_results.md S1a
        catch (Exception ex)
        {
            logger.LogError(ex, "ValidateAndApply pre-validate failed for {OperationName}", operationName);
            return new ApplyOutcome(null, ToolErrorMapper.ToResultError(ex, workspaceManager, $"{operationName} pre-validate"), dryRun);
        }

        if (validation.IsError)
        {
            var detail = validation.Diagnostics;
            var detailString = describeValidationFailure != null
                ? await describeValidationFailure(validation, cancellationToken)
                : validation.Diagnostics.ToJson();
            var validationError = new ResultError(
                ToolErrorCode.ValidationFailed,
                $"{operationName}: the change was valid and matched its target(s), but introduces new compiler errors - change not applied. " +
                $"Fix the issue(s) below and retry.",
                detailString,
                detail.Cast<object>().ToList());
            // Foreign-agent findings for the files that failed the compile gate; null when there are none so single-agent output is unchanged.
            var validationFindings = EditAttributionFindings.ForCurrentCaller(
                workspaceManager, detail.Select(d => d.FilePath.ToString()));
            return new ApplyOutcome(null, validationError with { Findings = validationFindings.Count > 0 ? validationFindings.ToList() : null }, dryRun, Validation: validation);
        }

        // The chokepoint re-runs this gate if another agent writes before this change lands.
        ToolCallContext.Current?.MarkGatePassed(allRemovePaths);

        if (dryRun)
        {
            var previewDiff = returnDiff ? await BuildDiffAsync(workspaceManager, changes, cancellationToken) : null;
            return new ApplyOutcome(null, null, true, previewDiff,
                LineChanges: ComputeLineChanges(changes, deletePaths, p => beforeTexts.GetValueOrDefault(p)));
        }

        var applyResult = await workspaceManager.ApplyProposedChangesAsync(
            changes, retryCount: 3, validateChanges: false, rollbackOnPartialFailure: true,
            progress: progress, cancellationToken: cancellationToken, deletePaths: deletePaths,
            operationLabel: operationName);

        if (applyResult.IsError)
        {
            // The chokepoint's own EOL backstop reports RefusalCode; surface it as its distinct code.
            var applyError = applyResult.RefusalCode != null
                ? new ResultError(applyResult.RefusalCode, applyResult.Summary)
                : new ResultError(ToolErrorCode.Exception, $"{operationName} apply failed", Detail: applyResult.Summary);
            // ConcurrentEditRefused's Summary already names the writer, so the lookup is skipped for it.
            var refusedPaths = applyResult.RefusalCode == ToolErrorCode.ConcurrentEditRefused
                ? Array.Empty<string>()
                : applyResult.FailedFiles.Keys.Select(k => k.ToString());
            var refusalFindings = EditAttributionFindings.ForCurrentCaller(workspaceManager, refusedPaths);
            return new ApplyOutcome(null, applyError with { Findings = refusalFindings.Count > 0 ? refusalFindings.ToList() : null }, false);
        }

        var changeId = Guid.NewGuid().ToString("n")[..8];
        var blob = await OperationBlobWriter.WriteApplyBlobAsync(
            operationName, changeId, applyResult, workspaceManager.GetSolutionRoot(), logger);

        if (!ChangedContentOptions.InlineOnApply && applyResult.SucceededFiles.Count > 0)
        {
            DetachChangedFiles(changes);
        }

        var appliedDiff = returnDiff ? BuildDiffFromPreImages(changes, applyResult.PreImages) : null;
        var lineChanges = ComputeLineChanges(changes, deletePaths, p =>
            applyResult.PreImages != null && applyResult.PreImages.TryGetValue(p, out var pre) ? pre : beforeTexts.GetValueOrDefault(p));

        // The blob-integrity invariant, enforced where both facts are known at once: an apply that
        // wrote files and issues a changeId must have a resolvable blob. This return value used to
        // be discarded entirely, which is why run 20260910-013550-398 saw status:"applied" with a
        // confident undo instruction and no blob on disk.
        //
        // Deliberately not thrown: the files are already written, and reporting a landed edit as
        // failed would invite the model to retry it -> a corruption path worse than a missing undo
        // record. Instead the result tells the truth (applied, not reversible) and the breaker
        // refuses every subsequent mutation.
        if (blob.IsIntegrityFailure)
        {
            var reason = blob.Diagnostic ?? "the operation blob could not be written";
            ((IUnrecoverableBreaker)workspaceManager).Trip(operationName, changeId, reason);

            // No changeId: one UndoLastApply cannot resolve is worse than none.
            return new ApplyOutcome(null, null, false, appliedDiff, reason, LineChanges: lineChanges, Findings: applyResult.Findings);
        }

        // Nothing was written, so no changeId should be issued either. Previously one was minted
        // unconditionally, which meant any operation producing an empty change set -> most commonly
        // one whose refactoring feature is disabled in SentinelConfiguration, e.g. ExtractInterface,
        // which returns an empty dictionary rather than an error -> reported status:"applied" and
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
                "nothing to undo. If you expected a change, the operation matched no target - or " +
                "its refactoring feature is disabled on this server (see the Features tool).",
                LineChanges: lineChanges, Findings: applyResult.Findings);
        }

        return new ApplyOutcome(changeId, null, false, appliedDiff, LineChanges: lineChanges, Findings: applyResult.Findings);
    }

    /// <summary>The name the written files are detached under: the payload lands at <c>successData.changedFiles</c> in the result store.</summary>
    public const string ChangedFilesPayloadName = "changedFiles";

    /// <summary>
    /// Declares the updated text of every written file as the stored (not inline) <c>successData.changedFiles</c> payload of
    /// the current call, one <see cref="ChangedFile"/> per entry. A no-op when no <see cref="ToolCallContext"/> is set.
    /// A name can be detached once per call, but a tool may apply several batches in one call (per-project loops such as
    /// QualifyCrefs and the test-category apply), so later batches append to the list the first one detached.
    /// </summary>
    public static void DetachChangedFiles(IReadOnlyDictionary<FilePathWrapper, string> changes)
    {
        var context = ToolCallContext.Current;
        if (context is null || changes.Count == 0)
        {
            return;
        }

        var rows = changes.Select(entry => new ChangedFile((string)entry.Key, entry.Value)).ToList();
        // The batches of one call are applied one after another, so the list needs no lock.
        if (context.Detached.FirstOrDefault(detached => detached.Name == ChangedFilesPayloadName).Payload is List<ChangedFile> existing)
        {
            existing.AddRange(rows);
            return;
        }

        context.Detach(ChangedFilesPayloadName, rows);
    }

    /// <summary>
    /// Reads each path's current text: the committed workspace document when there is one (what the
    /// tool computed its edit against), otherwise the file on disk, otherwise null (a brand-new file).
    /// </summary>
    private static async Task<Dictionary<string, string?>> ReadBeforeTextsAsync(
        IWorkspaceManager workspaceManager,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>();
        Solution? solution = null;
        foreach (var path in paths)
        {
            if (result.ContainsKey(path))
            {
                continue;
            }

            // The workspace document first: it is the text the tool actually computed its edit
            // against, and it also covers in-memory solutions whose paths are not real disk files.
            // Disk is the fallback for files that are not workspace documents.
            solution ??= await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var docId = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            if (docId != null)
            {
                result[path] = (await solution.GetDocument(docId)!.GetTextAsync(cancellationToken)).ToString();
            }
            else if (File.Exists(path))
            {
                result[path] = await File.ReadAllTextAsync(path, cancellationToken);
            }
            else
            {
                result[path] = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Per-file added/removed line counts for every written file, then every deleted file (whose
    /// every line counts as removed).
    /// </summary>
    private static List<FileLineChange> ComputeLineChanges(
        Dictionary<FilePathWrapper, string> changes,
        IReadOnlyCollection<FilePathWrapper>? deletePaths,
        Func<string, string?> beforeLookup)
    {
        var stats = new List<FileLineChange>();
        foreach (var (path, after) in changes)
        {
            stats.Add(FileLineChange.Compute(path, beforeLookup(path), after));
        }

        foreach (var path in deletePaths ?? [])
        {
            var before = beforeLookup(path);
            if (before != null)
            {
                stats.Add(FileLineChange.Compute(path, before, string.Empty));
            }
        }

        return stats;
    }

    /// <summary>
    /// Per-file added/removed line counts of <paramref name="changes"/> against each file's current text, for the preview-only
    /// (autoStage=false) path that has the proposed new text but no <see cref="ApplyOutcome"/>. The pre-image is read like
    /// <see cref="BuildDiffAsync"/> does (committed workspace document, else disk); a path that is neither is a new file (null before).
    /// </summary>
    public static async Task<List<FileLineChange>> ComputeLineChangesAsync(
        IWorkspaceManager workspaceManager,
        Dictionary<FilePathWrapper, string> changes,
        CancellationToken cancellationToken)
    {
        var beforeTexts = await ReadBeforeTextsAsync(workspaceManager, changes.Keys.Select(k => (string)k), cancellationToken);
        return ComputeLineChanges(changes, null, p => beforeTexts.GetValueOrDefault(p));
    }

    public static async Task<string> BuildDiffAsync(
        IWorkspaceManager workspaceManager,
        Dictionary<FilePathWrapper, string> changes,
        CancellationToken cancellationToken)
    {
        var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
