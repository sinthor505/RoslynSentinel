using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// The agent-loop half of a SubAgentTool/SubAgentEvalTool call, shared by both: argument validation, and
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
                "model is empty. Pass the name of a model already loaded on the model host, exactly as the host lists it.");
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

    /// <summary>Description of the shared <c>hostAlias</c> tool parameter.</summary>
    public const string HostAliasDescription = "Optional alias of a model host from eval-hosts.json (for example 112u or 85lms). Omit for the server's own host (--llm-base-url). " +
        "The host must answer GET <baseUrl>/models within 5 s and list the model, or the call is refused before anything is launched.";

    /// <summary>How long the pre-run <c>GET &lt;baseUrl&gt;/models</c> may take before the host counts as unreachable.</summary>
    public static readonly TimeSpan HostCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most model ids named in a "models present" error.</summary>
    private const int MaxListedModels = 30;

    /// <summary>
    /// Resolves the model host for one call and checks it before anything is launched, so a refusal costs
    /// no worktree or child server. A blank <paramref name="hostAlias"/> selects the process-wide
    /// <see cref="LlmOptions.BaseUrl"/>; an alias is looked up in eval-hosts.json (<paramref name="hostTablePath"/>,
    /// default <see cref="EvalHostTable.DefaultPath"/>). The check is one <c>GET &lt;baseUrl&gt;/models</c> (OpenAI-compatible,
    /// served by both llama-server and LM Studio) that must answer within <see cref="HostCheckTimeout"/> and list
    /// <paramref name="model"/>. Returns the base URL to use, or an error naming what was found.
    /// </summary>
    public static async Task<(string BaseUrl, ResultError? Error)> ResolveAndCheckHostAsync(
        string? hostAlias, string model, CancellationToken cancellationToken, string? hostTablePath = null)
    {
        var baseUrl = LlmOptions.BaseUrl;
        if (!string.IsNullOrWhiteSpace(hostAlias))
        {
            IReadOnlyList<EvalHost> table;
            try
            {
                table = EvalHostTable.Load(hostTablePath);
            }
            catch (InvalidOperationException)
            {
                // The exception text names the file's full path; keep it out of the tool result.
                return (baseUrl, new ResultError(ToolErrorCode.PreconditionNotMet,
                    $"hostAlias '{hostAlias.Trim()}' could not be resolved: {EvalHostTable.FileName} is present but failed validation. " +
                    "The alias table is read from the file beside the server binary; omit hostAlias to use the process-wide host."));
            }

            var host = EvalHostTable.FindByAlias(table, hostAlias);
            if (host is null)
            {
                var known = table.Count == 0
                    ? $"none ({EvalHostTable.FileName} was not found beside the server binary, or lists no hosts)"
                    : string.Join(", ", table.Select(h => h.Alias));
                return (baseUrl, new ResultError(ToolErrorCode.InvalidArgument,
                    $"hostAlias '{hostAlias.Trim()}' is not in {EvalHostTable.FileName}. Known aliases: {known}. " +
                    $"Omitting hostAlias uses the process-wide host {LlmOptions.BaseUrl}."));
            }

            baseUrl = host.BaseUrl;
        }

        // The check applies to the process-wide host as well as to an alias: a down host or a missing model
        // is the same wasted worktree build either way, and no existing test drives SubAgent through a host.
        return (baseUrl, await CheckHostAsync(baseUrl, model, cancellationToken));
    }

    private static async Task<ResultError?> CheckHostAsync(string baseUrl, string model, CancellationToken cancellationToken)
    {
        var modelsUrl = baseUrl.TrimEnd('/') + "/models";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HostCheckTimeout);

        string body;
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(modelsUrl, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ResultError(ToolErrorCode.PreconditionNotMet,
                    $"The model host did not accept the pre-run check: GET {modelsUrl} answered {(int)response.StatusCode} {response.StatusCode}. Nothing was launched.");
            }

            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ResultError(ToolErrorCode.PreconditionNotMet,
                $"The model host is unreachable: GET {modelsUrl} did not answer within {HostCheckTimeout.TotalSeconds:0} s (TimeoutException). Nothing was launched.");
        }
        catch (Exception ex)
        {
            var kind = ex.InnerException is null ? ex.GetType().Name : $"{ex.GetType().Name} ({ex.InnerException.GetType().Name})";
            return new ResultError(ToolErrorCode.PreconditionNotMet,
                $"The model host is unreachable: GET {modelsUrl} failed with {kind}. Nothing was launched.");
        }

        var listed = LlamaServerModels.Parse(body);
        if (LlamaServerModels.FindListed(listed, model) is not null)
        {
            return null;
        }

        var present = listed.Count == 0
            ? "none (the host answered but listed no models)"
            : string.Join(", ", listed.Take(MaxListedModels).Select(m => m.Id)) + (listed.Count > MaxListedModels ? $" ... ({listed.Count} in total)" : "");
        return new ResultError(ToolErrorCode.NotFound,
            $"Model '{model}' is not in the model list of {modelsUrl}. Models present: {present}. Nothing was launched.");
    }

    /// <summary>
    /// Waits, bounded by SolutionLoadWait.DefaultTimeout like GetCurrentSolutionAsync, for a queued or running solution load; returns a SolutionLoadTimeout error when it is still running, else null.
    /// </summary>
    public static async Task<ResultError?> AwaitSolutionLoadAsync(IWorkspaceManager workspaceManager, CancellationToken cancellationToken)
    {
        if (workspaceManager.SolutionLoadStatus != SolutionLoadStatus.Loading)
        {
            return null;
        }

        var status = await workspaceManager.WaitForLoadAsync(SolutionLoadWait.DefaultTimeout, cancellationToken);
        return status == SolutionLoadStatus.Loading
            ? new ResultError(ToolErrorCode.SolutionLoadTimeout, SolutionNotLoadedMessage.LoadWaitTimedOutFor(workspaceManager, SolutionLoadWait.DefaultTimeout))
            : null;
    }

    /// <summary>
    /// Runs <paramref name="prompt"/> against the child server with the dispatched <paramref name="model"/>.
    /// Writes agent.log and transcript.json under <see cref="SubAgentChildServer.LogDirectory"/>.
    /// </summary>
    /// <param name="systemPrompt">System prompt for the dispatched model.</param>
    /// <param name="baseUrl">Model host /v1 root for this call, as returned by <see cref="ResolveAndCheckHostAsync"/>.</param>
    public static async Task<AgentRunResult> RunAsync(
        SubAgentChildServer child, string systemPrompt, string prompt, string model, string baseUrl, int maxTokensPerTurn,
        int? turnCap, int? wallClockCapMinutes, CancellationToken cancellationToken)
    {
        // A fresh HttpClient and LmStudioAgentClient per call: nothing about the model is shared
        // between two calls in flight in this process, which is the whole reason the model is an
        // explicit constructor argument rather than the process-wide LlmOptions.Model.
        // TODO SubAgent/SubAgentEval do not coordinate with roslynsentinel-evalqueue.ps1, so a call can still land on a host that is mid-queue (the pre-run check only proves the host answers and lists the model)
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
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
            $"The solution is already loaded - do not call {ToolCallHint.Call("LoadSolution")} or {ToolCallHint.Call("ListWorkspaceSolutions")}, " +
            "go straight to reading/editing.\n\n" + prompt;

        return await runner.RunAsync(systemPrompt, userPrompt, child.LogDirectory, cancellationToken, runId: child.RunId);
    }
}
