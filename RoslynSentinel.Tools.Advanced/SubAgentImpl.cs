using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// Plain DI-constructed implementation backing <see cref="SubAgentTools"/>: dispatches a prompt to a
/// real LM Studio model in a throwaway worktree and returns what the model said. Unlike
/// <see cref="SubAgentEvalImpl"/> it has no build/test opinion.
/// </summary>
public sealed class SubAgentImpl(IWorkspaceManager workspaceManager, ILogger logger)
{
    /// <summary>
    /// System prompt for the dispatched model. Not the coding-agent prompt: that one tells the model to
    /// make the smallest change and verify it with a build, which is wrong for a general question.
    /// </summary>
    public const string SystemPrompt = """
        You are a sub-agent, dispatched by another agent to complete one task and report back. You work
        inside a real C#/.NET repository through the RoslynSentinel MCP tool server. You have NO shell,
        terminal, or filesystem access outside these tools.

        - Do what the task asks, and only that. Make no edits unless the task asks for them.
        - Never invent a tool name, parameter, or symbol you have not seen in this session. If you are
          unsure something exists, look it up first.
        - If a tool call fails, read the error and adjust; do not repeat the same failing call.
        - Your final message is the only thing returned to the agent that dispatched you. Put the
          complete answer in it. Any edits you make happen in a throwaway copy of the repository that
          is deleted when you finish, so they are not returned.
        - If you are blocked or cannot complete the task as described, say so explicitly rather than
          guessing.
        """;

    /// <summary>Appended to the prompt when <see cref="SubAgentResponseFormat.Json"/> is requested.</summary>
    public const string JsonInstruction =
        "\n\nYour final message must be a single JSON value and nothing else: no prose before or after it, " +
        "and no markdown code fence.";

    /// <summary>The prompt as the dispatched model sees it, with any response-format instruction applied.</summary>
    public static string ApplyResponseFormat(string prompt, SubAgentResponseFormat responseFormat) =>
        responseFormat == SubAgentResponseFormat.Json ? prompt + JsonInstruction : prompt;

    public async Task<SentinelCallToolResult<SubAgentResult>> SubAgent(
        ToolCallReason reason, string prompt, string model, int maxTokensPerTurn,
        int? turnCap, int? wallClockCapMinutes, SubAgentResponseFormat responseFormat, CancellationToken cancellationToken)
    {
        var validationError = SubAgentModelRun.Validate(prompt, model, maxTokensPerTurn, turnCap, wallClockCapMinutes);
        if (validationError is not null)
        {
            return Failure(validationError);
        }

        var solutionPath = workspaceManager.SolutionPath;
        if (string.IsNullOrEmpty(solutionPath))
        {
            return Failure(new ResultError(ToolErrorCode.SolutionNotLoaded,
                "No solution is loaded. Call LoadSolution first; SubAgent branches the loaded solution's git repo."));
        }

        var runId = SubAgentRunNaming.NewRunId();
        logger.LogInformation("SubAgent {RunId}: starting for model {Model}. Reason: {Reason}", runId, model, reason);

        try
        {
            await using var child = await new SubAgentChildServerLauncher(logger)
                .LaunchAsync(solutionPath, runId, "HEAD", model, cancellationToken);

            var run = await SubAgentModelRun.RunAsync(
                child, SystemPrompt, ApplyResponseFormat(prompt, responseFormat), model, maxTokensPerTurn,
                turnCap, wallClockCapMinutes, cancellationToken);

            var output = run.Transcript.Turns.Count > 0
                ? run.Transcript.Turns[^1].ModelMessage.Content ?? ""
                : "";

            return await SentinelCallToolResult<SubAgentResult>.ForPossiblyLargeDataAsync(
                new SubAgentResult
                {
                    Output = output,
                    Converged = run.Converged,
                    StopReason = run.StopReason.ToString(),
                    TurnCount = run.TurnCount,
                    TranscriptPath = run.TranscriptPath,
                },
                workspaceManager.GetSolutionRoot(), "SubAgentResult", ResultWrapperType.Raw,
                cancellationToken: cancellationToken);
        }
        catch (SubAgentLaunchException ex)
        {
            logger.LogWarning(ex, "SubAgent {RunId}: launch failed.", runId);
            return Failure(new ResultError(ex.ErrorCode, ex.Message, ex.Detail));
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled; the await-using above has already torn the child server down.
            throw;
        }
        catch (Exception ex)
        {
            // Never surface the raw exception or stack trace; the type name plus the server log is
            // enough for a follow-up.
            logger.LogError(ex, "SubAgent {RunId}: failed unexpectedly.", runId);
            return Failure(new ResultError(ToolErrorCode.Exception,
                $"SubAgent failed unexpectedly ({ex.GetType().Name}). Run id {runId}; see the server log and the run's agent.log for detail."));
        }
    }

    private static SentinelCallToolResult<SubAgentResult> Failure(ResultError error) =>
        new() { IsSuccess = false, ErrorData = error };
}
