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
        Dictionary<FilePath, string> changes,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default)
    {
        DiagnosticReport validation;
        try
        {
            validation = await validationEngine.ValidateChangesAsync(changes, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ValidateAndApply pre-validate failed for {OperationName}", operationName);
            return new ApplyOutcome(null, ToolErrorMapper.ToResultError(ex, workspaceManager, $"{operationName} pre-validate"), dryRun);
        }

        if (!validation.Success)
        {
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} introduces new compiler errors — change not applied. " +
                $"Fix diagnostics and retry: {validation.Diagnostics.ToJson()}"), dryRun);
        }

        if (dryRun)
        {
            var previewDiff = returnDiff ? await BuildDiffAsync(workspaceManager, changes, cancellationToken) : null;
            return new ApplyOutcome(null, null, true, previewDiff);
        }

        var applyResult = await workspaceManager.ApplyProposedChangesAsync(
            changes, retryCount: 3, validateChanges: false, rollbackOnPartialFailure: true,
            progress: progress, cancellationToken: cancellationToken);

        if (!applyResult.Success)
        {
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} apply failed: {applyResult.Summary}"), false);
        }

        var changeId = Guid.NewGuid().ToString("n")[..8];
        await OperationBlobWriter.WriteApplyBlobAsync(operationName, changeId, applyResult, workspaceManager.GetSolutionRoot());

        var appliedDiff = returnDiff ? BuildDiffFromPreImages(changes, applyResult.PreImages) : null;
        return new ApplyOutcome(changeId, null, false, appliedDiff);
    }

    public static async Task<string> BuildDiffAsync(
        IWorkspaceManager workspaceManager,
        Dictionary<FilePath, string> changes,
        CancellationToken cancellationToken)
    {
        var solution = await workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
        Dictionary<FilePath, string> changes,
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
