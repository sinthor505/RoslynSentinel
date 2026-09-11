namespace RoslynSentinel.Common;

/// <summary>
/// Result summary returned by the write-through refactoring tools (ValidateAndApplyAsync).
/// The change is already written to disk (or, when <see cref="DryRun"/> is true, validated
/// but deliberately not written) — there is no separate apply step.
/// </summary>
public record AppliedChangeSummary(
    string? ChangeId,
    List<FilePathWrapper> AffectedFiles,
    string Description,
    bool DryRun,
    string? Diff = null,
    int? WorkspaceVersion = null
)
{
    /// <summary>
    /// Machine-parseable outcome — "dry_run_ok" when validated but deliberately not written,
    /// "no_changes" when the operation produced nothing to write, otherwise "applied".
    /// </summary>
    /// <remarks>
    /// "no_changes" exists because this used to report "applied" for an empty change set, which
    /// told the agent its edit had landed when no file had been touched. Most reachable via an
    /// operation whose refactoring feature is disabled in SentinelConfiguration — those return an
    /// empty dictionary rather than an error.
    /// </remarks>
    public string Status => DryRun
        ? "dry_run_ok"
        : AffectedFiles.Count == 0 ? "no_changes" : "applied";

    public string Note
    {
        get
        {
            if (DryRun)
            {
                return "Validated — introduces no new compiler errors. Not written to disk (dryRun=true). Re-call with dryRun=false to apply.";
            }

            // No ChangeId on a non-dry-run means no undo record exists — either because nothing was
            // written (a no-op) or because the blob write failed. Either way, advertising
            // UndoLastApply here would be a lie, and used to be one: run 20260910-013550-398
            // returned this note alongside a changeId UndoLastApply could never resolve. The two
            // cases are distinguished by AffectedFiles, and the specific reason travels in
            // ApplyOutcome.NotReversibleReason for callers that surface it.
            if (!string.IsNullOrEmpty(ChangeId))
            {
                return $"Written to disk. Call UndoLastApply(changeId: \"{ChangeId}\") to revert if needed.";
            }

            return AffectedFiles.Count == 0
                ? "No changes were produced, so nothing was written and there is nothing to undo. If you expected a change, the operation matched no target — or its refactoring feature is disabled on this server (check the Features tool)."
                : "Written to disk, but NOT reversible: the server could not record an undo entry for this change, so UndoLastApply cannot revert it. Revert manually (e.g. via version control) if needed.";
        }
    }
}
