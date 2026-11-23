using System.ComponentModel;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tools.Advanced;

[McpServerToolType]
public class SubAgentTools
{
    private readonly SubAgentImpl _impl;

    public SubAgentTools(IWorkspaceManager workspaceManager, ILogger<SubAgentTools> logger)
    {
        _impl = new SubAgentImpl(workspaceManager, logger);
    }

    [McpServerTool(Name = "SubAgent")]
    [Produces(DataTag.Report)]
    [Description("Runs a prompt against a real model on a model host in a fresh, worktree-isolated copy of the repo " +
        "(branched from committed HEAD) and returns the model's final message. General-purpose dispatch with no " +
        "build or test step. The worktree is deleted afterward, so any edits the model makes are discarded; only " +
        "its final message and a transcript path come back. The dispatched model is given the Claude tool " +
        "surface minus SubAgent and SubAgentEval, so it cannot dispatch further. Slow: builds a private server " +
        "before the model runs. Only usable when the loaded solution is the RoslynSentinel repo. Before any launch it " +
        "checks that the model host answers and lists the model, and refuses otherwise.")]
    public Task<SentinelCallToolResult<SubAgentResult>> SubAgentTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Full task text for the dispatched model, inlined by value - never a file path.")]
        string prompt,
        [Description("Name of a model already loaded on the model host, exactly as the host lists it (same value as --llm-model).")]
        string model,
        [Description("Required, no default. Per-turn output token ceiling sent to the model host. Size it for the prompt's expected reasoning load; too small truncates a turn mid-reasoning.")]
        int maxTokensPerTurn,
        [Description("Most model turns allowed. Omit for the default (25).")]
        int? turnCap = null,
        [Description("Wall-clock limit for the model run, in minutes. Omit for the default (10).")]
        int? wallClockCapMinutes = null,
        [Description("Text (default): the model's final message verbatim. Json: the prompt asks the model to end with a single JSON value; the message is still returned verbatim, unvalidated.")]
        SubAgentResponseFormat responseFormat = SubAgentResponseFormat.Text,
        [Description("Whether to dispose the worktree after the model run.")]
        bool disposeWorktree = true,
        [Description(SubAgentModelRun.HostAliasDescription)]
        string? hostAlias = null,
        CancellationToken cancellationToken = default)
        => _impl.SubAgent(reason, prompt, model, maxTokensPerTurn, turnCap, wallClockCapMinutes, responseFormat, disposeWorktree, hostAlias, cancellationToken);
}
