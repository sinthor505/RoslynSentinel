using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Result summary returned by the write-through refactoring tools (ValidateAndApplyAsync).
/// The change is already written to disk (or, when <see cref="DryRun"/> is true, validated
/// but deliberately not written) -> there is no separate apply step.
/// </summary>
/// <remarks>
/// Build it with <see cref="From(ApplyOutcome, List{FilePathWrapper}, string, Dictionary{FilePathWrapper, string}?, bool, int?, List{AppliedEditSpan}?)"/>
/// (or the preview-only overload) so no site forgets <see cref="LineChanges"/> or <see cref="Findings"/>. The primary constructor is
/// deliberately not [Obsolete]: tests construct it directly and positional deconstruction relies on it, so
/// <c>From</c> is the convention for tool code rather than a compiler-enforced rule.
/// </remarks>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<AppliedEditSpan>? EditSpans = null,
    // Write-chokepoint findings copied from ApplyOutcome.Findings; not serialized here, the result envelope lifts them (SentinelCallToolResult.Findings).
    [property: JsonIgnore] List<Finding>? Findings = null
) : IOperationResult
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
    /// Builds the summary of a validate-then-apply call from its <see cref="ApplyOutcome"/>: ChangeId, DryRun, Diff, LineChanges and Findings are
    /// copied from <paramref name="apply"/>, so a site cannot forget the last two. <paramref name="validated"/> defaults to true (the outcome
    /// went through the compile gate); pass the files' new text as <paramref name="changedContent"/> when the tool echoes it.
    /// A site that drops the diff unless the caller asked for it uses <c>From(...) with { Diff = ... }</c>.
    /// </summary>
    public static AppliedChangeSummary From(
        ApplyOutcome apply,
        List<FilePathWrapper> affectedFiles,
        string description,
        Dictionary<FilePathWrapper, string>? changedContent = null,
        bool validated = true,
        int? workspaceVersion = null,
        List<AppliedEditSpan>? editSpans = null)
        => new(
            apply.ChangeId,
            affectedFiles,
            description,
            apply.DryRun,
            apply.Diff,
            workspaceVersion,
            changedContent,
            validated,
            apply.LineChanges,
            editSpans,
            apply.Findings);

    /// <summary>
    /// Builds the summary of a preview-only result that has no <see cref="ApplyOutcome"/> (autoStage=false: proposed contents, nothing written,
    /// <see cref="ChangeId"/> null). Pass <paramref name="lineChanges"/> from <c>ValidateAndApplyHelper.ComputeLineChangesAsync</c>;
    /// null/empty is right for a no-op with no affected files. <paramref name="validated"/> defaults to false (nothing went through the compile gate).
    /// </summary>
    public static AppliedChangeSummary From(
        List<FileLineChange>? lineChanges,
        List<Finding>? findings,
        List<FilePathWrapper> affectedFiles,
        string description,
        bool dryRun = false,
        string? diff = null,
        Dictionary<FilePathWrapper, string>? changedContent = null,
        bool validated = false)
        => new(
            null,
            affectedFiles,
            description,
            dryRun,
            diff,
            null,
            changedContent,
            validated,
            lineChanges,
            null,
            findings);

    /// <summary>
    /// true when no-stage result (autoStage=false): proposed contents only, nothing written to disk.
    /// Indicated by !DryRun, !Validated, ChangedContent present, ChangeId null.
    /// </summary>
    private bool NotWritten => !DryRun && !Validated && ChangedContent != null && ChangeId == null;

    /// <summary>
    /// Inline only for no-stage results (Validated false) and when ChangedContentOptions.InlineOnApply is on; otherwise null.
    /// A dry run is compact too: lineChanges say what would change and the diff (returnDiff: true) shows it, never the whole file.
    /// A real apply stays null: its updated text is the stored successData.changedFiles payload (largeResult.resultId).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<FilePathWrapper, string>? ChangedContent { get; init; } = (DryRun || Validated) && !ChangedContentOptions.InlineOnApply ? null : ChangedContent;

    public string Note
    {
        get
        {
            if (DryRun)
            {
                // returnDiff may be hidden from the lean schema profile, but the server still accepts it at call time.
                var preview = ChangedContent != null
                    ? "The proposed file contents are in changedContent."
                    : !string.IsNullOrEmpty(Diff)
                        ? "The diff is the preview."
                        : "Compact preview: lineChanges lists the per-file changes. To see the diff, re-call the same tool with the same arguments plus returnDiff: true (keep dryRun=true).";
                return $"Validated - introduces no new compiler errors. Not written to disk (dryRun=true). {preview} Re-call with dryRun=false to apply.";
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
                return $"Written to disk. Call {ToolCallHint.Call("UndoLastApply", ("changeId", ChangeId))} to revert if needed." + NotEchoedHint();
            }

            return AffectedFiles.Count == 0
                ? "No changes were produced, so nothing was written and there is nothing to undo. If you expected a change, the operation matched no target - or its refactoring feature is disabled on this server (check the Features tool)."
                : "Written to disk, but NOT reversible: the server could not record an undo entry for this change, so UndoLastApply cannot revert it. Revert manually (e.g. via version control) if needed." + NotEchoedHint();
        }
    }

    // Failures never reach this summary: they arrive as a ResultError on the wrapper. Only the four
    // success-shaped Status strings are produced here, so IsError is always false.

    /// <summary><see cref="IOperationResult.Status"/>: AlreadyInState for "no_changes", else Success. Explicit because the public <see cref="Status"/> is a string. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    ResultStatus IOperationResult.Status => Status == "no_changes" ? ResultStatus.AlreadyInState : ResultStatus.Success;

    /// <summary><see cref="IOperationResult.IsError"/>: always false (see the comment above). Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => false;

    /// <summary><see cref="Code"/>: always null. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => null;

    /// <summary><see cref="Message"/>: the <see cref="Note"/> text. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Note;

    private string NotEchoedHint()
    {
        if (ChangedContent != null)
        {
            return "";
        }

        // The real apply detached its written files (ValidateAndApplyHelper.DetachChangedFiles); this summary has no id of its
        // own, because the DTO is serialized before the marshaller stores the payload. The id is the response's largeResult.resultId.
        // Without a ToolCallContext (an engine or a test calling the code directly) nothing was detached, so the read-it-yourself note below applies.
        if (ToolCallContext.Current?.Detached.Any(detached => detached.Name == ValidateAndApplyHelper.ChangedFilesPayloadName) == true)
        {
            return $" The updated content is not echoed; it is stored with this response (largeResult.resultId): fetch it with {LargeResultHints.FetchCall("<largeResult.resultId>", path: "successData." + ValidateAndApplyHelper.ChangedFilesPayloadName)}, or read the file with {ToolCallHint.Call("ReadFile")}, GetMethodSource or {ToolCallHint.Call("Member", ("operation", "view"))}.";
        }

        return Validated ? $" The updated content is not echoed; read it with {ToolCallHint.Call("ReadFile")}, GetMethodSource or {ToolCallHint.Call("Member", ("operation", "view"))}." : "";
    }
}
