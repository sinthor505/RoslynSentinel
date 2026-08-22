namespace RoslynSentinel.Common;

/// <summary>
/// Result summary returned by the write-through refactoring tools (ValidateAndApplyAsync).
/// The change is already written to disk (or, when <see cref="DryRun"/> is true, validated
/// but deliberately not written) — there is no separate apply step.
/// </summary>
public record AppliedChangeSummary(
    string? ChangeId,
    List<FilePath> AffectedFiles,
    string Description,
    bool DryRun,
    string? Diff = null,
    int? WorkspaceVersion = null
)
{
    /// <summary>Machine-parseable outcome — "applied" once written to disk, "dry_run_ok" when validated but not written.</summary>
    public string Status => DryRun ? "dry_run_ok" : "applied";

    public string Note => DryRun
        ? "Validated — introduces no new compiler errors. Not written to disk (dryRun=true). Re-call with dryRun=false to apply."
        : $"Written to disk. Call UndoLastApply(changeId: \"{ChangeId}\") to revert if needed.";
}
