using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

/// <summary>One project's QualifyCrefsTool outcome.</summary>
/// <param name="Project">The project.</param>
/// <param name="Status">"dry_run_ok" (edits would be written), "applied", "no_changes" or "failed" (a failed project wrote nothing).</param>
/// <param name="Qualified">Bare member crefs qualified in this project.</param>
/// <param name="SkippedAlreadyQualified">Crefs already qualified, left alone.</param>
/// <param name="SkippedTypeOrPredefined">Crefs naming a type, type parameter, namespace or alias, left alone.</param>
/// <param name="SkippedUnresolved">Crefs that do not bind, left alone.</param>
/// <param name="DroppedByRebind">Edits discarded because the cref no longer binds to the same symbol after the edits.</param>
/// <param name="ChangeId">Undo id for this project's batch, when something was written.</param>
/// <param name="AffectedFiles">Files written (or that a dry run would write); empty for a failed project.</param>
/// <param name="Diff">Diff of the batch, when returnDiff is set.</param>
/// <param name="LineChanges">Per-file lines added and removed.</param>
/// <param name="ErrorCode">Error code when the project's batch was rejected.</param>
/// <param name="Error">Why the project's batch was rejected.</param>
/// <param name="ErrorDetail">Compiler-error detail (truncated) when the batch was rejected.</param>
public sealed record QualifyCrefsProjectOutcome(
    string Project,
    string Status,
    int Qualified,
    int SkippedAlreadyQualified,
    int SkippedTypeOrPredefined,
    int SkippedUnresolved,
    int DroppedByRebind,
    string? ChangeId,
    List<string> AffectedFiles,
    string? Diff,
    List<FileLineChange>? LineChanges,
    string? ErrorCode,
    string? Error,
    string? ErrorDetail) : IOperationResult
{
    /// <summary>True when the project's batch was rejected (Status is "failed").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Status == "failed";

    /// <summary>ToolErrorCode from ErrorCode, or Exception when missing; null when not an error.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? (ErrorCode ?? ToolErrorCode.Exception) : null;

    /// <summary>Error text when IsError; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => IsError ? Error : null;

    /// <summary>
    /// Explicit: the public Status is the string verdict, so the IOperationResult view is exposed here. "failed" maps
    /// through ErrorCode; "no_changes" is AlreadyInState; anything else is Success.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    ResultStatus IOperationResult.Status => Status switch
    {
        "failed" => ResultStatusExtensions.FromFlag(true, ErrorCode),
        "no_changes" => ResultStatus.AlreadyInState,
        _ => ResultStatus.Success,
    };
}

/// <summary>
/// Result of QualifyCrefsTool: the applied-change summary fields (changeId, affectedFiles, description, dryRun, validated,
/// workspaceVersion, status, note) plus totals and per-project outcomes. One write batch (and so one undo changeId)
/// exists per project, so <see cref="ChangeIds"/> lists them all; <see cref="ChangeId"/> is set only when exactly one
/// batch was written.
/// </summary>
public sealed record QualifyCrefsSummary(
    string? ChangeId,
    List<string> ChangeIds,
    List<FilePathWrapper> AffectedFiles,
    string Description,
    bool DryRun,
    bool Validated,
    int? WorkspaceVersion,
    int Qualified,
    int SkippedAlreadyQualified,
    int SkippedTypeOrPredefined,
    int SkippedUnresolved,
    int DroppedByRebind,
    int FailedProjects,
    List<QualifyCrefsProjectOutcome> Projects) : IOperationResult
{
    /// <summary>"dry_run_ok", "applied", "partially_applied" (some project failed, others landed) or "no_changes".</summary>
    public string Status => FailedProjects > 0 && AffectedFiles.Count > 0
        ? "partially_applied"
        : AffectedFiles.Count == 0 ? "no_changes" : DryRun ? "dry_run_ok" : "applied";

    /// <summary>True when any project failed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Projects.Any(p => p.IsError);

    /// <summary>
    /// Explicit: the public Status is the string verdict, so the IOperationResult view is exposed here. Any failed project
    /// makes the summary Failed; "no_changes" is AlreadyInState; anything else is Success.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    ResultStatus IOperationResult.Status => IsError ? ResultStatus.Failed : Status == "no_changes" ? ResultStatus.AlreadyInState : ResultStatus.Success;

    /// <summary>ToolErrorCode.Exception when IsError; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    /// <summary>"N of M failed" over the per-project outcomes when any failed; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => ResultStatusExtensions.FailureCountMessage(Projects.Count(p => !p.IsError), FailedProjects);

    public string Note => DryRun
        ? "Dry run: nothing was written. Pass dryRun: false to apply; each project is validated and written as one batch."
        : ChangeIds.Count == 0
            ? "Nothing was written: no bare member crefs needed qualifying, or every project was rejected. Run with dryRun: true to see the plan."
            : $"Written to disk. Call UndoLastApply(changeId: ...) once per batch to revert; one batch per project: {string.Join(", ", ChangeIds)}.";
}

/// <summary>One project's raw run: the per-document counters summed, the files that carry edits, and the batch outcome.</summary>
internal sealed record QualifyCrefsProjectRun(
    string Project,
    IReadOnlyList<string> Files,
    int Qualified,
    int SkippedAlreadyQualified,
    int SkippedTypeOrPredefined,
    int SkippedUnresolved,
    int DroppedByRebind,
    ApplyOutcome? Apply);

internal static class QualifyCrefsReport
{
    private const int MaxErrorDetailChars = 1500;

    public static QualifyCrefsProjectOutcome ToOutcome(QualifyCrefsProjectRun run, bool dryRun)
    {
        var apply = run.Apply;
        var failed = apply?.Error is not null;
        var status = failed ? "failed" : run.Files.Count == 0 ? "no_changes" : dryRun ? "dry_run_ok" : "applied";

        return new QualifyCrefsProjectOutcome(
            run.Project,
            status,
            run.Qualified,
            run.SkippedAlreadyQualified,
            run.SkippedTypeOrPredefined,
            run.SkippedUnresolved,
            run.DroppedByRebind,
            apply?.ChangeId,
            failed ? new List<string>() : run.Files.ToList(),
            apply?.Diff,
            apply?.LineChanges,
            apply?.Error?.ErrorCode,
            apply?.Error?.Message,
            Truncate(apply?.Error?.Detail));
    }

    public static QualifyCrefsSummary Build(IReadOnlyList<QualifyCrefsProjectOutcome> projects, bool dryRun, int? workspaceVersion)
    {
        var landed = projects.Where(p => !p.IsError).ToList();
        var changeIds = projects.Where(p => p.ChangeId is not null).Select(p => p.ChangeId!).ToList();
        var affected = landed
            .SelectMany(p => p.AffectedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new FilePathWrapper(f))
            .ToList();
        var qualified = landed.Sum(p => p.Qualified);
        var touched = landed.Count(p => p.AffectedFiles.Count > 0);

        var description = dryRun
            ? $"Dry run: {qualified} bare member cref(s) would be qualified in {affected.Count} file(s) across {touched} project(s). Nothing was written."
            : $"Qualified {qualified} bare member cref(s) in {affected.Count} file(s) across {touched} project(s).";

        return new QualifyCrefsSummary(
            changeIds.Count == 1 ? changeIds[0] : null,
            changeIds,
            affected,
            description,
            dryRun,
            Validated: true,
            workspaceVersion,
            qualified,
            landed.Sum(p => p.SkippedAlreadyQualified),
            landed.Sum(p => p.SkippedTypeOrPredefined),
            landed.Sum(p => p.SkippedUnresolved),
            landed.Sum(p => p.DroppedByRebind),
            projects.Count(p => p.IsError),
            projects.ToList());
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxErrorDetailChars ? value : value[..MaxErrorDetailChars] + "... (truncated)";
}
