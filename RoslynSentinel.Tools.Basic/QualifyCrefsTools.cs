using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>QualifyCrefsTool</c>: rewrites bare member crefs in XML doc comments to qualified ones (<c>Foo</c> to <c>A.Foo</c>).
/// Dry run by default (read-only plan); <c>dryRun: false</c> applies it through the validated write path, one compile-gated
/// batch per project. Never commits.
/// </summary>
[McpServerToolType]
public class QualifyCrefsTools
{
    private readonly QualifyCrefsImpl _impl;

    public QualifyCrefsTools(QualifyCrefsImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "QualifyCrefs")]
    [Produces(DataTag.Report)]
    [Description("Qualify bare member crefs in XML doc comments: rewrites a bare member cref such as <see cref=\"Foo\"/> to its qualified form " +
        "(for example <see cref=\"A.Foo\"/>) using the semantic model. Scope is file, project or solution (scopeName is a file path for file, " +
        "a project name for project, and omitted for solution). containingType optionally limits the rewrite to members of one type. " +
        "dryRun defaults to true (plan only, nothing written): review the report first, then call again with dryRun: false to apply. " +
        "returnDiff adds the per-file diff to the report. Apply makes one validated write batch per project: a project whose batch would not " +
        "compile is reported and left unchanged while the others still apply. Never commits. " +
        "Skips type crefs, crefs that are already qualified, and crefs that do not resolve, and reports counts for each (qualified, " +
        "skippedAlreadyQualified, skippedTypeOrPredefined, skippedUnresolved, droppedByRebind). " +
        "Idempotent: re-running after an apply produces no further edits. " +
        "Returns one changeId per project batch (revertible with UndoLastApply) and a status of dry_run_ok, applied, partially_applied or no_changes.")]
    public async Task<SentinelCallToolResult<object>> QualifyCrefsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.Scope)] ToolScope scope,
        [Description("Required for scope=file (a file path) and scope=project (a project name). Omit for scope=solution.")][ExternalInputRequired(DataTag.SourceFilepath), ExternalInputRequired(DataTag.ProjectName)] string? scopeName = null,
        [Description("Optional. Only qualify crefs whose member belongs to this containing type (type name or display string).")] string? containingType = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        await _impl.QualifyAsync(scope, scopeName, containingType, dryRun, returnDiff, cancellationToken);
}
