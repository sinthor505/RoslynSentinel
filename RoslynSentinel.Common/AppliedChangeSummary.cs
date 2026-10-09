using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Result summary returned by the write-through refactoring tools (ValidateAndApplyAsync).
/// The change is already written to disk (or, when <see cref="DryRun"/> is true, validated
/// but deliberately not written) -> there is no separate apply step.
/// </summary>
public record AppliedChangeSummary(
    string? ChangeId,
    List<FilePathWrapper> AffectedFiles,
    string Description,
    bool DryRun,
    string? Diff = null,
    int? WorkspaceVersion = null,
    Dictionary<FilePathWrapper, string>? ChangedContent = null,
    bool Validated = false,
    List<FileLineChange>? LineChanges = null,
    string? ChangedContentResultId = null
)
{
    /// <summary>
    /// Machine-parseable outcome -> "dry_run_ok" when validated but deliberately not written,
    /// "not_written" when autoStage=false (tool only proposed contents, did not write),
    /// "no_changes" when the operation produced nothing to write, otherwise "applied".
    /// </summary>
    /// <remarks>
    /// "no_changes" exists because this used to report "applied" for an empty change set, which
    /// told the agent its edit had landed when no file had been touched. Most reachable via an
    /// operation whose refactoring feature is disabled in SentinelConfiguration -> those return an
    /// empty dictionary rather than an error.
    /// "not_written" is reported when the tool only proposed file contents (autoStage=false) and wrote nothing to disk.
    /// </remarks>
    public string Status => DryRun
        ? "dry_run_ok"
        : NotWritten ? "not_written" : AffectedFiles.Count == 0 ? "no_changes" : "applied";

    /// <summary>
    /// true when no-stage result (autoStage=false): proposed contents only, nothing written to disk.
    /// Indicated by !DryRun, !Validated, ChangedContent present, ChangeId null.
    /// </summary>
    private bool NotWritten => !DryRun && !Validated && ChangedContent != null && ChangeId == null;

    /// <summary>Inline only for dry runs, no-stage results (Validated false) and when ChangedContentOptions.InlineOnApply is on; otherwise null for a real apply - see ChangedContentResultId.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<FilePathWrapper, string>? ChangedContent { get; init; } = Validated && !DryRun && !ChangedContentOptions.InlineOnApply ? null : ChangedContent;

    public string Note
    {
        get
        {
            if (DryRun)
            {
                return "Validated - introduces no new compiler errors. Not written to disk (dryRun=true). Re-call with dryRun=false to apply.";
            }

            if (NotWritten)
            {
                return "Nothing was written (autoStage=false). The proposed file contents are in changedContent; apply them yourself, or re-call with autoStage: true to have the server apply and validate the change.";
            }

            // No ChangeId on a non-dry-run means no undo record exists -> either because nothing was
            // written (a no-op) or because the blob write failed. Either way, advertising
            // UndoLastApply here would be a lie, and used to be one: run 20260910-013550-398
            // returned this note alongside a changeId UndoLastApply could never resolve. The two
            // cases are distinguished by AffectedFiles, and the specific reason travels in
            // ApplyOutcome.NotReversibleReason for callers that surface it.
            if (!string.IsNullOrEmpty(ChangeId))
            {
                return $"Written to disk. Call UndoLastApply(changeId: \"{ChangeId}\") to revert if needed." + NotEchoedHint();
            }

            return AffectedFiles.Count == 0
                ? "No changes were produced, so nothing was written and there is nothing to undo. If you expected a change, the operation matched no target - or its refactoring feature is disabled on this server (check the Features tool)."
                : "Written to disk, but NOT reversible: the server could not record an undo entry for this change, so UndoLastApply cannot revert it. Revert manually (e.g. via version control) if needed." + NotEchoedHint();
        }
    }

    private string NotEchoedHint()
    {
        if (ChangedContent != null)
        {
            return "";
        }

        if (!string.IsNullOrEmpty(ChangedContentResultId))
        {
            return $" The updated content is not echoed; fetch it with GetLargeResult(resultId: \"{ChangedContentResultId}\"), or read the file with ReadFile, GetMethodSource or Member(operation: view).";
        }

        return Validated ? " The updated content is not echoed; read it with ReadFile, GetMethodSource or Member(operation: view)." : "";
    }
}
