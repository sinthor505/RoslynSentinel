using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// CORRECTION vs. plan doc Decision 1 (see WorkspaceFileEditImpl.cs doc comment for full detail):
/// scoped down to the tools actually still live on SentinelWorkspaceTools today
/// (RetryFailedChanges, UndoLastApply, ReadFile, ReplaceSnippet, CreateFile). ApplyDiff/
/// ApplyUnifiedDiff/WriteFile/DeleteFile live on the separate, plan-unnamed
/// SentinelWholeFileWriteTools.cs and are out of scope here.
/// </summary>
[McpServerToolType]
public class WorkspaceFileEditTools
{
    private readonly WorkspaceFileEditImpl _impl;

    public WorkspaceFileEditTools(IWorkspaceManager workspaceManager, WorkspaceReadNavigationImpl readNav, ILogger logger,
        ValidationEngine validationEngine, SymbolNavigationEngine symbolNavigationEngine, WriteToolAdviceHelper writeAdvice)
    {
        _impl = new WorkspaceFileEditImpl(workspaceManager, readNav, logger, validationEngine, symbolNavigationEngine, writeAdvice);
    }

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content - no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChanges(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _impl.RetryFailedChanges(reason, specificFiles, retryCount, cancellationToken);

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: ApplyDiff, refactoring-tool writes, and batch-first tools.")]
    public Task<SentinelCallToolResult<object>> UndoLastApply(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _impl.UndoLastApply(reason, changeId, cancellationToken);

    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file - useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/largeresults and returned as a resultId (see GetMethodSource) instead of inline text.")]
    public Task<SentinelCallToolResult<object>> ReadFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null,
        [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null,
        CancellationToken cancellationToken = default)
        => _impl.ReadFile(reason, filepath, startLine, endLine, cancellationToken);

    [McpServerTool(Name = "ReplaceSnippet")]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact block of text with another in a single file, for localized edits. For a structural change, prefer the matching Roslyn tool (RenameSymbol, ChangeSignature, ExtractMethodSafe, Member, etc.) instead. For multiple small edits - in the same file or across files - pass 'edits' instead of the singular filepath/oldContent/newContent params. By default this also delta-compiles the edited project(s) plus every project that transitively references them BEFORE writing, and REJECTS the change if it introduces any new compiler error.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippet(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? edits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _impl.ReplaceSnippet(reason, action, filepath, oldContent, newContent, lineBefore, lineAfter, edits, validateOnApply, returnDiff, cancellationToken);

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: namespaceName/typeKind/typeName are required for a .cs
    // file (to seed a valid compilation unit) and ignored for every other file extension -> a model
    // creating a non-.cs file can omit all three, but a model creating a .cs file must supply all
    // three or the call fails, and nothing besides the description text signals that split.
    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file. Fails if the file already exists - this tool never overwrites or writes free-form whole-file content. Parent directories are created automatically if missing.")]
    public Task<SentinelCallToolResult<object>> CreateFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Required for .cs files, ignored otherwise. Namespace to seed the file with (e.g. 'RoslynSentinel.Tests.Battery').")] string? namespaceName = null,
        [Description("Required for .cs files, ignored otherwise. Kind of top-level type to seed the file with - this seeds a valid compilation unit plus one empty top-level type declaration (e.g. 'public class Foo\\n{\\n}'), so Member(add) can immediately populate members inside it. Use staticClass for a static utility/helper class (e.g. static test helpers, extension-method containers) - static is only valid on classes, not the other kinds. For a second top-level type in the same file, add it afterward with Member(add, containerName: null, newMemberSource: \"...\").")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files, ignored otherwise. Name of the top-level type to seed the file with (e.g. 'Foo').")] string? typeName = null,
        CancellationToken cancellationToken = default)
        => _impl.CreateFile(reason, filepath, namespaceName, typeKind, typeName, cancellationToken);
}
