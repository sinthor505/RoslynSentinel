using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// CORRECTION vs. plan doc Decision 1 (see WorkspaceFileEditImpl.cs doc comment for full detail):
/// scoped down to the 3 tools actually still live on SentinelWorkspaceTools today
/// (RetryFailedChanges, UndoLastApply, ReadFile). ApplyDiff/ApplyUnifiedDiff/WriteFile/DeleteFile
/// live on the separate, plan-unnamed SentinelWholeFileWriteTools.cs and are out of scope here.
/// </summary>
[McpServerToolType]
public class WorkspaceFileEditTools
{
    private readonly WorkspaceFileEditImpl _impl;

    public WorkspaceFileEditTools(IWorkspaceManager workspaceManager, WorkspaceReadNavigationImpl readNav, ILogger logger)
    {
        _impl = new WorkspaceFileEditImpl(workspaceManager, readNav, logger);
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
}
