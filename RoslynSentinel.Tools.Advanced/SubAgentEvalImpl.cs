using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// Plain DI-constructed implementation backing <see cref="SubAgentEvalTools"/>: runs a prompt against
/// a real LM Studio model in a throwaway worktree, then builds and tests whatever the model left.
/// </summary>
public sealed class SubAgentEvalImpl(IWorkspaceManager workspaceManager, ILogger logger)
{
    public async Task<SentinelCallToolResult<SubAgentEvalResult>> SubAgentEval(
        ToolCallReason reason, string prompt, string model, int maxTokensPerTurn,
        string? baseBranch, int? turnCap, int? wallClockCapMinutes, CancellationToken cancellationToken)
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
                SolutionNotLoadedMessage.Build(workspaceManager.LoadState) + " SubAgentEval branches the loaded solution's git repo."));
        }

        var runId = SubAgentRunNaming.NewRunId();
        logger.LogInformation("SubAgentEval {RunId}: starting for model {Model}. Reason: {Reason}", runId, model, reason);

        try
        {
            await using var child = await new SubAgentChildServerLauncher(logger)
                .LaunchAsync(solutionPath, runId, string.IsNullOrWhiteSpace(baseBranch) ? "HEAD" : baseBranch, model, cancellationToken);

            var run = await SubAgentModelRun.RunAsync(
                child, AgentSystemPrompts.CodingAgent, prompt, model, maxTokensPerTurn, turnCap, wallClockCapMinutes, cancellationToken);

            // Read what the model touched before the post-run Build/RunTest snapshots, so the
            // snapshots' own side effects can never be mistaken for the model's edits.
            var touchedPaths = child.GitWorktreeManager.GetDirtyPaths(child.WorktreePath);

            var buildText = await TryCallAsync(child.McpClient, "Build", new Dictionary<string, object?>
            {
                ["reason"] = "SubAgentEval: post-run build snapshot.",
                ["level"] = "fullBuild",
                // The counts are all that is read. A full-solution build's stdout/warnings still
                // push the result over the inline-size threshold, in which case the child returns a
                // pointer to a file in its worktree; ResolveOffloadedResult reads it back.
                ["maxDetails"] = 1,
            }, cancellationToken);
            buildText = SubAgentEvalResult.ResolveOffloadedResult(buildText, child.WorktreePath);

            var result = SubAgentEvalResult.Build(run, buildText, testResultJson: null, touchedPaths);
            if (result.BuildSucceeded)
            {
                var testText = await TryCallAsync(child.McpClient, "RunTest", new Dictionary<string, object?>
                {
                    ["reason"] = "SubAgentEval: post-run test snapshot.",
                    ["summary"] = true,
                    ["maxDetails"] = 1,
                }, cancellationToken);
                testText = SubAgentEvalResult.ResolveOffloadedResult(testText, child.WorktreePath);
                result = SubAgentEvalResult.Build(run, buildText, testText, touchedPaths);
            }

            return await SentinelCallToolResult<SubAgentEvalResult>.ForPossiblyLargeDataAsync(
                result, workspaceManager.GetSolutionRoot(), "SubAgentEvalResult", ResultWrapperType.Raw,
                cancellationToken: cancellationToken);
        }
        catch (SubAgentLaunchException ex)
        {
            logger.LogWarning(ex, "SubAgentEval {RunId}: launch failed.", runId);
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
            logger.LogError(ex, "SubAgentEval {RunId}: failed unexpectedly.", runId);
            return Failure(new ResultError(ToolErrorCode.Exception,
                $"SubAgentEval failed unexpectedly ({ex.GetType().Name}). Run id {runId}; see the server log and the run's agent.log for detail."));
        }
    }

    /// <summary>
    /// Calls a tool on the child server and returns its text, or null when the call itself failed
    /// (a dead child process, a protocol error). A missing snapshot is reported as a note on the
    /// result rather than failing a run whose model work already finished.
    /// </summary>
    private async Task<string?> TryCallAsync(
        McpClient client, string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.CallToolAsync(
                toolName, arguments, progress: null, options: null, cancellationToken: cancellationToken);
            return ChildToolResult.Text(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SubAgentEval: the child server's {Tool} call failed.", toolName);
            return null;
        }
    }

    private static SentinelCallToolResult<SubAgentEvalResult> Failure(ResultError error) =>
        new() { IsError = true, ErrorData = error };
}
