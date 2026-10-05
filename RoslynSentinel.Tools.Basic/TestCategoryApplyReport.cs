using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>One test project's apply outcome as reported by <c>TagTestCategories(dryRun: false)</c>.</summary>
/// <param name="Project">The test project.</param>
/// <param name="Status">"applied", "no_changes" or "failed" (a failed project wrote nothing).</param>
/// <param name="ClassLevelAdded">Class-level attributes written.</param>
/// <param name="MethodLevelAdded">Method-level attributes written.</param>
/// <param name="RemovedStale">Marked stale attributes removed.</param>
/// <param name="SkippedExisting">Edits the planner skipped because the category was already present or covered.</param>
/// <param name="Failed">Planned edits that did not land (the whole project's edits when its batch was rejected).</param>
/// <param name="ChangeId">Undo id for this project's batch, when something was written.</param>
/// <param name="AffectedFiles">Files written.</param>
/// <param name="ErrorCode">Error code when the project's batch was rejected.</param>
/// <param name="Error">Why the project's batch was rejected (for example new compiler errors).</param>
/// <param name="ErrorDetail">Compiler-error detail (truncated) when the batch was rejected.</param>
/// <param name="SkippedEdits">Edits skipped at apply time, each with a reason.</param>
public sealed record TestCategoryProjectOutcome(
    string Project,
    string Status,
    int ClassLevelAdded,
    int MethodLevelAdded,
    int RemovedStale,
    int SkippedExisting,
    int Failed,
    string? ChangeId,
    List<string> AffectedFiles,
    string? ErrorCode,
    string? Error,
    string? ErrorDetail,
    List<TestCategorySkippedEdit> SkippedEdits);

/// <summary>An edit that was planned but not applied, with why.</summary>
public sealed record TestCategorySkippedEdit(string Kind, string Level, string File, int Line, string Category, string Reason);

/// <summary>
/// Result of <c>TagTestCategories(dryRun: false)</c>: the standard applied-change summary fields (changeId, affectedFiles,
/// description, dryRun, validated, workspaceVersion, status, note) plus totals and per-project counts. One write batch
/// (and so one undo changeId) exists per test project, so <see cref="ChangeIds"/> lists them all; <see cref="ChangeId"/>
/// is set only when exactly one batch was written.
/// </summary>
public sealed record TestCategoryAppliedSummary(
    string? ChangeId,
    List<string> ChangeIds,
    List<FilePathWrapper> AffectedFiles,
    string Description,
    bool DryRun,
    bool Validated,
    int? WorkspaceVersion,
    int ClassLevelAdded,
    int MethodLevelAdded,
    int RemovedStale,
    int SkippedExisting,
    int Failed,
    List<TestCategoryProjectOutcome> Projects,
    List<string> Warnings)
{
    /// <summary>"applied", "partially_applied" (some project failed, others landed) or "no_changes".</summary>
    public string Status => Failed > 0 && AffectedFiles.Count > 0
        ? "partially_applied"
        : AffectedFiles.Count == 0 ? "no_changes" : "applied";

    public string Note => ChangeIds.Count == 0
        ? "Nothing was written: the plan had no edits, or every planned attribute already exists. Run with dryRun: true to see the plan."
        : $"Written to disk. Call UndoLastApply(changeId: ...) once per batch to revert; one batch per test project: {string.Join(", ", ChangeIds)}.";
}

internal static class TestCategoryApplyReport
{
    private const int MaxErrorDetailChars = 1500;

    public static TestCategoryAppliedSummary Build(TestCategoryPlan plan, TestCategoryApplyResult result, int? workspaceVersion)
    {
        var projects = result.Projects.Select(ToOutcome).ToList();
        var changeIds = projects.Where(p => p.ChangeId is not null).Select(p => p.ChangeId!).ToList();
        var affected = result.Projects
            .SelectMany(p => p.AffectedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new FilePathWrapper(f))
            .ToList();

        var classAdded = projects.Sum(p => p.ClassLevelAdded);
        var methodAdded = projects.Sum(p => p.MethodLevelAdded);
        var removed = projects.Sum(p => p.RemovedStale);
        var description = $"Applied test category attributes: {classAdded} class-level added, {methodAdded} method-level added, {removed} stale removed across {projects.Count(p => p.Status == "applied")} test project(s).";

        return new TestCategoryAppliedSummary(
            changeIds.Count == 1 ? changeIds[0] : null,
            changeIds,
            affected,
            description,
            DryRun: false,
            Validated: true,
            workspaceVersion,
            classAdded,
            methodAdded,
            removed,
            projects.Sum(p => p.SkippedExisting),
            projects.Sum(p => p.Failed),
            projects,
            plan.Warnings.ToList());
    }

    /// <summary>True when at least one project had edits to write and none of them landed.</summary>
    public static bool AllFailed(TestCategoryApplyResult result) =>
        result.Projects.Any(p => p.Error is not null) && result.Projects.All(p => p.Error is not null || p.AffectedFiles.Count == 0);

    public static TestCategoryProjectOutcome ToOutcome(TestCategoryProjectApplyResult p)
    {
        var status = p.Error is not null ? "failed" : p.AffectedFiles.Count > 0 ? "applied" : "no_changes";
        return new TestCategoryProjectOutcome(
            p.ProjectName,
            status,
            p.ClassLevelAdded,
            p.MethodLevelAdded,
            p.RemovedStale,
            p.SkippedExisting,
            p.Failed,
            p.ChangeId,
            p.AffectedFiles.ToList(),
            p.Error?.ErrorCode,
            p.Error?.Message,
            Truncate(p.Error?.Detail),
            p.SkippedEdits
                .Select(s => new TestCategorySkippedEdit(
                    s.Edit.Kind.ToString(), s.Edit.Level.ToString(), s.Edit.FilePath, s.Edit.Line, s.Edit.CategoryName, s.Reason))
                .ToList());
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxErrorDetailChars ? value : value[..MaxErrorDetailChars] + "... (truncated)";
}
