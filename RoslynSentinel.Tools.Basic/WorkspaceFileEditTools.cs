using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// CORRECTION vs. plan doc Decision 1 (see WorkspaceFileEditImpl.cs doc comment for full detail):
/// scoped down to the tools actually still live on WorkspaceTools today
/// (RetryFailedChangesTool, UndoLastApplyTool, ReadFileTool, ReplaceSnippetTool, CreateFileTool). ApplyDiffTool/
/// ApplyUnifiedDiffTool/WriteFileTool/DeleteFileTool live on the separate, plan-unnamed
/// WholeFileWriteTools.cs and are out of scope here.
/// </summary>
[McpServerToolType]
public class WorkspaceFileEditTools
{
    private readonly WorkspaceFileEditImpl _impl;

    public WorkspaceFileEditTools(WorkspaceFileEditImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content - no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChangesTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _impl.RetryFailedChanges(reason, specificFiles, retryCount, cancellationToken);

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: ApplyDiff, refactoring-tool writes, and batch-first tools.")]
    public Task<SentinelCallToolResult<object>> UndoLastApplyTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _impl.UndoLastApply(reason, changeId, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file - useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/largeresults and returned as a resultId (see GetMethodSource) instead of inline text. To read a single member, pass memberName (optionally containerName).")]
    public Task<SentinelCallToolResult<object>> ReadFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null,
        [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Read just this member's source (method, property, field, constructor) instead of the whole file. Cannot be combined with startLine/endLine.")] string? memberName = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Only with memberName: the type that holds the member, to disambiguate same-named members.")] string? containerName = null,
        CancellationToken cancellationToken = default)
        => _impl.ReadFile(reason, filePath, startLine, endLine, memberName, containerName, cancellationToken);

    [McpServerTool(Name = "ReplaceSnippet")]
    [SupportsBatching]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact text block with another in a file, for small localized edits. Prefer a structural Roslyn tool for structural changes. By default the edited project(s) and their dependents are delta-compiled BEFORE writing, and the change is rejected if it introduces a new compiler error. For edits larger than the size limit or whole-member rewrites, use Member(operation: replace) instead.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippetTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)][Description("File to edit. Do not combine with batchEdits; each batch edit carries its own filePath.")] string? filePath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ReplaceSnippetWithinMember)] string? withinMember = null,
        [Description(ToolParams.ReplaceSnippetMatchLine)] int? matchLine = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? batchEdits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _impl.ReplaceSnippet(reason, action, filePath, oldContent, newContent, lineBefore, lineAfter, withinMember, matchLine, batchEdits, validateOnApply, returnDiff, cancellationToken);

    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file. Fails if the file already exists - this tool never overwrites or writes free-form whole-file content. Parent directories are created automatically if missing. For a new file with its full content in one call, use WriteFile(operation: CreateFile, content: ...).")]
    public Task<SentinelCallToolResult<object>> CreateFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("For .cs files: namespace to seed the file with (e.g. 'RoslynSentinel.Tests.Battery.Basic'); omit it to create the type in the global namespace. Ignored for other file types.")] string? namespaceName = null,
        [Description("Required for .cs files, ignored otherwise. Kind of top-level type to seed the file with - this seeds a valid compilation unit plus one empty top-level type declaration (e.g. 'public class Foo\\n{\\n}'), so Member(add) can immediately populate members inside it. Use staticClass for a static utility/helper class (e.g. static test helpers, extension-method containers) - static is only valid on classes, not the other kinds. For a second top-level type in the same file, add it afterward with Member(add, containerName: null, newMemberSource: \"...\").")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files, ignored otherwise. Name of the top-level type to seed the file with (e.g. 'Foo').")] string? typeName = null,
        CancellationToken cancellationToken = default)
        => _impl.CreateFile(reason, filePath, namespaceName, typeKind, typeName, cancellationToken);
}
