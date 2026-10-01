using Microsoft.Extensions.Logging;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// The agent-loop half of a SubAgent/SubAgentEval call, shared by both: argument validation, and
/// running the dispatched model against a launched <see cref="SubAgentChildServer"/>.
/// </summary>
public static class SubAgentModelRun
{
    /// <summary>Same defaults <see cref="ModelAgentRunner"/> applies when a caller does not choose.</summary>
    public const int DefaultTurnCap = 25;

    /// <summary>Same default <see cref="ModelAgentRunner"/> applies when a caller does not choose.</summary>
    public const int DefaultWallClockCapMinutes = 10;

    /// <summary>
    /// Consecutive identical tool failures that end the run. Matches PlanStepRunner: an unattended
    /// dispatched model that is looping on one error should be cut short, not left to burn the turn cap.
    /// </summary>
    public const int RepeatedFailureLimit = 3;

    /// <summary>
    /// Checks the arguments both tools share. Returns an actionable error naming the offending
    /// parameter, or null when everything is acceptable.
    /// </summary>
    public static ResultError? Validate(string? prompt, string? model, int maxTokensPerTurn, int? turnCap, int? wallClockCapMinutes)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ResultError(ToolErrorCode.InvalidArgument,
                "prompt is empty. Pass the full task text for the dispatched model, inlined by value (never a file path).");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ResultError(ToolErrorCode.InvalidArgument,
                "model is empty. Pass the name of a model already loaded in LM Studio, exactly as LM Studio lists it.");
        }

        if (maxTokensPerTurn < 1)
        {
            return new ResultError(ToolErrorCode.InvalidArgument,
                $"maxTokensPerTurn is {maxTokensPerTurn}; it must be a positive per-turn output token ceiling. " +
                "Size it for the prompt's expected reasoning load (a step needing substantial up-front reasoning needs more headroom).");
        }

        if (turnCap is < 1)
        {
            return new ResultError(ToolErrorCode.InvalidArgument,
                $"turnCap is {turnCap}; omit it for the default ({DefaultTurnCap}) or pass a positive number.");
        }

        if (wallClockCapMinutes is < 1)
        {
            return new ResultError(ToolErrorCode.InvalidArgument,
                $"wallClockCapMinutes is {wallClockCapMinutes}; omit it for the default ({DefaultWallClockCapMinutes}) or pass a positive number.");
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="prompt"/> against the child server with the dispatched <paramref name="model"/>.
    /// Writes agent.log and transcript.json under <see cref="SubAgentChildServer.LogDirectory"/>.
    /// </summary>
    /// <param name="systemPrompt">System prompt for the dispatched model.</param>
    public static async Task<AgentRunResult> RunAsync(
        SubAgentChildServer child, string systemPrompt, string prompt, string model, int maxTokensPerTurn,
        int? turnCap, int? wallClockCapMinutes, CancellationToken cancellationToken)
    {
        // A fresh HttpClient and LmStudioAgentClient per call: nothing about the model is shared
        // between two calls in flight in this process, which is the whole reason the model is an
        // explicit constructor argument rather than the process-wide LlmOptions.Model.
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600)),
        };

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new FlushingFileLoggerProvider(Path.Combine(child.LogDirectory, "agent.log"))));

        var agentClient = new LmStudioAgentClient(httpClient, model, loggerFactory.CreateLogger<LmStudioAgentClient>());
        var runner = new ModelAgentRunner(
            agentClient,
            child.McpClient,
            repeatedFailureLimit: RepeatedFailureLimit,
            maxTokensPerTurn: maxTokensPerTurn,
            turnCap: turnCap ?? DefaultTurnCap,
            wallClockCap: TimeSpan.FromMinutes(wallClockCapMinutes ?? DefaultWallClockCapMinutes),
            logger: loggerFactory.CreateLogger<ModelAgentRunner>());

        // Mirrors PlanStepRunner: the solution is already loaded by the launcher, and the task is
        // inlined by value so the model has no reason to go looking for it on disk.
        var userPrompt =
            "The solution is already loaded - do not call LoadSolution or ListWorkspaceSolutions, " +
            "go straight to reading/editing.\n\n" + prompt;

        return await runner.RunAsync(systemPrompt, userPrompt, child.LogDirectory, cancellationToken, runId: child.RunId);
    }
}
