using System.ComponentModel;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Experiment-only batch-only edit tool (EditSnippets); registered only under the EditSnippetsExperiment mode. See proposal_edit_snippets_batch_only_ab_test.
/// </summary>
[McpServerToolType]
public sealed class EditSnippetsTools
{
    private readonly WorkspaceFileEditImpl _impl;

    public EditSnippetsTools(WorkspaceFileEditImpl impl)
    {
        _impl = impl;
    }

    // Experiment (proposal_edit_snippets_batch_only_ab_test): shares ReplaceSnippet's impl; rewrite names so this arm never points at the hidden tool.
    // TODO remove EditSnippets or replace ReplaceSnippet after the A/B decision
    [McpServerTool(Name = "EditSnippets")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces exact text blocks in one or more files as one atomic change. Every edit is an item with its own filePath, oldContent and newContent; a single edit is a list of one. By default the edited project(s) and their dependents are delta-compiled BEFORE writing, and the change is rejected if it introduces a new compiler error.")]
    public async Task<SentinelCallToolResult<ReplaceSnippetResult>> EditSnippetsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        [Description("The edits, applied atomically against the original file contents; overlapping edits are rejected. One item per text block; each item has its own filePath.")] List<SnippetEdit> edits,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _impl.ReplaceSnippet(reason, action, null, null, null, null, null, null, null, edits, validateOnApply, returnDiff, cancellationToken);
        return RewriteForEditSnippets(result);
    }

    // Experiment helper for EditSnippets (proposal_edit_snippets_batch_only_ab_test): the impl's error text names ReplaceSnippet and batchEdits, which the hidden arm must not show.
    private static SentinelCallToolResult<ReplaceSnippetResult> RewriteForEditSnippets(SentinelCallToolResult<ReplaceSnippetResult> result)
    {
        if (!result.IsError || result.ErrorData is not { } error)
        {
            return result;
        }

        string message = error.Message
            .Replace("ReplaceSnippet", "EditSnippets", StringComparison.Ordinal)
            .Replace("batchEdits", "edits", StringComparison.Ordinal);
        return result with { ErrorData = error with { Message = message } };
    }
}
