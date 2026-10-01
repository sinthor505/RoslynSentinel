using System.ComponentModel;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tools.Advanced;

[McpServerToolType]
public class SubAgentEvalTools
{
    private readonly SubAgentEvalImpl _impl;

    public SubAgentEvalTools(IWorkspaceManager workspaceManager, ILogger<SubAgentEvalTools> logger)
    {
        _impl = new SubAgentEvalImpl(workspaceManager, logger);
    }

    [McpServerTool(Name = "SubAgentEval")]
    [Produces(DataTag.Report)]
    [Description("Runs a prompt against a real LM Studio model in a fresh, worktree-isolated copy of the repo " +
        "(branched from committed HEAD, so uncommitted edits in the live session are not visible to it), then " +
        "builds and tests the result. Returns a structured pass/fail report: build/test outcome, turn count, " +
        "stop reason, files touched, and a failure excerpt when the run did not converge cleanly. The worktree " +
        "is deleted afterward; the transcript path in the result survives. Slow: builds a private server, runs " +
        "the model, then builds and runs the tests. Use for an ad-hoc micro-eval question (\"can this model do " +
        "X\"), not for a scoped multi-step plan (use PlanStepRunner for that). Only usable when the loaded " +
        "solution is the RoslynSentinel repo.")]
    public Task<SentinelCallToolResult<SubAgentEvalResult>> SubAgentEval(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Full task text for the dispatched model, inlined by value - never a file path.")]
        string prompt,
        [Description("Name of a model already loaded in LM Studio, exactly as LM Studio lists it (same value as --llm-model).")]
        string model,
        [Description("Required, no default. Per-turn output token ceiling sent to LM Studio. Size it for the prompt's expected reasoning load; too small truncates a turn mid-reasoning.")]
        int maxTokensPerTurn,
        [Description("Git ref the worktree branches from. Omit for HEAD.")]
        string? baseBranch = null,
        [Description("Most model turns allowed. Omit for the default (25).")]
        int? turnCap = null,
        [Description("Wall-clock limit for the model run, in minutes (build and test time excluded). Omit for the default (10).")]
        int? wallClockCapMinutes = null,
        CancellationToken cancellationToken = default)
        => _impl.SubAgentEval(reason, prompt, model, maxTokensPerTurn, baseBranch, turnCap, wallClockCapMinutes, cancellationToken);
}
