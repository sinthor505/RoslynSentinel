using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Common;

/// <summary>One latched tool of one agent, as McpServerStatus reports it (only when --loop-latch is on).</summary>
/// <param name="AgentId">The caller agent id, or <see cref="ToolLoopLatch.DefaultAgentKey"/> for a call with none.</param>
/// <param name="Tool">The tool that is refused until another tool succeeds.</param>
/// <param name="FailureCount">How many same-way failures latched it.</param>
/// <param name="ErrorCode">The error code the failures shared.</param>
public sealed record LoopLatchAgentState(string AgentId, string Tool, int FailureCount, string ErrorCode);

/// <summary>
/// The server loop latch (proposal_loop_latch.md): when the same tool fails the same way
/// <see cref="LoopLatchOptions.TripThreshold"/> times with no successful call in between, that tool is disabled for that
/// caller agent until any other tool succeeds. A refused call is not executed. State is per agent id (a call with no agent id
/// shares the <see cref="DefaultAgentKey"/> key), so one agent's loop never disables a tool for its peers.
/// <para>
/// "Same way" = same tool name + same error code + (for a compile-gate rejection) the same first diagnostic id. Arguments are
/// not part of the key, so alternating argument errors count. Modelled on <see cref="OrientationCircuitBreaker"/>: trip, a
/// refusal text naming only callable tools, and an automatic release from the request filter (no manual reset tool).
/// The filter that drives it is <c>LoopLatchFilter</c> (Server.Advanced).
/// </para>
/// </summary>
public sealed class ToolLoopLatch
{
    /// <summary>Key shared by every call that carries no agent id.</summary>
    public const string DefaultAgentKey = "default";

    /// <summary>
    /// Stable text a refusal message carries right after the tool name; the harness detects a refusal by it
    /// (plus the TargetIneligible code), so do not reword it without changing <see cref="IsRefusalBody"/>'s callers.
    /// </summary>
    public const string RefusalMarker = " is DISABLED until another tool succeeds";

    private const string NextStepDirective = "You MUST do the next step now: ";

    private static readonly Regex DiagnosticId = new(@"\b[A-Z]{2,4}\d{4}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Read-only tools a refusal may point at; each is named only when callable (and is not the latched tool itself).
    private static readonly string[] ReadTools = ["ReadFile", "GetFileOutline", "Search"];

    private sealed class LatchedTool(string errorCode, int failureCount, string? nextStep)
    {
        public string ErrorCode { get; } = errorCode;
        public int FailureCount { get; } = failureCount;
        public string? NextStep { get; } = nextStep;
    }

    private sealed class AgentState
    {
        // Failures since the agent's last successful call, keyed by tool + error code + first diagnostic id.
        public Dictionary<string, int> FailureCounts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, LatchedTool> Latched { get; } = new(StringComparer.Ordinal);
    }

    private readonly int _tripThreshold;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, AgentState> _agents = new(StringComparer.Ordinal);

    public ToolLoopLatch(int tripThreshold = LoopLatchOptions.TripThreshold, ILogger? logger = null)
    {
        _tripThreshold = Math.Max(1, tripThreshold);
        _logger = logger;
    }

    /// <summary>The state key for a caller: the agent id, or <see cref="DefaultAgentKey"/> when it is null or blank.</summary>
    public static string AgentKey(string? agentId) => string.IsNullOrWhiteSpace(agentId) ? DefaultAgentKey : agentId;

    /// <summary>
    /// The refusal message when <paramref name="toolName"/> is latched for <paramref name="agentId"/> (the call must not run),
    /// otherwise null. <paramref name="isCallable"/> says whether a tool name is callable on this server (null = every tool is);
    /// the text names only callable tools.
    /// </summary>
    public string? TryRefuse(string? agentId, string toolName, Func<string, bool>? isCallable = null)
    {
        lock (_lock)
        {
            if (!_agents.TryGetValue(AgentKey(agentId), out var state) || !state.Latched.TryGetValue(toolName, out var latched))
            {
                return null;
            }

            return BuildRefusal(toolName, latched.FailureCount, latched.NextStep, isCallable);
        }
    }

    /// <summary>
    /// Records the outcome of an executed call. A success clears the agent's failure counts and releases every latch it holds
    /// (the latched tool itself is never executed, so a success is always another tool's). A failure counts toward its
    /// tool + error code + first diagnostic id and latches the tool at the threshold. Returns true only on the call that latches.
    /// </summary>
    /// <param name="errorMessage">The failure's message; its "Next step: " sentence, if any, goes into the refusal text.</param>
    /// <param name="diagnosticText">Text to search for the first diagnostic id of a compile-gate rejection (the message, plus structured detail when the id is only there); defaults to <paramref name="errorMessage"/>.</param>
    public bool RecordOutcome(string? agentId, string toolName, bool succeeded, string? errorCode, string? errorMessage, string? diagnosticText = null)
    {
        var key = AgentKey(agentId);
        lock (_lock)
        {
            if (succeeded)
            {
                _agents.Remove(key);
                return false;
            }

            if (!_agents.TryGetValue(key, out var state))
            {
                state = new AgentState();
                _agents[key] = state;
            }

            var way = string.Join('\u0001', toolName, errorCode ?? "", FirstDiagnosticId(errorCode, diagnosticText ?? errorMessage) ?? "");
            var count = state.FailureCounts.GetValueOrDefault(way) + 1;
            state.FailureCounts[way] = count;
            if (count < _tripThreshold || state.Latched.ContainsKey(toolName))
            {
                return false;
            }

            state.Latched[toolName] = new LatchedTool(errorCode ?? "", count, errorMessage is null ? null : LoopAssistTracker.ExtractNextStep(errorMessage));
            _logger?.LogWarning(
                "Loop latch TRIPPED for agent {Agent}: {Tool} failed the same way {Count} times (error code {ErrorCode}).",
                key, toolName, count, errorCode ?? "(none)");
            return true;
        }
    }

    /// <summary>The latched tools of every agent (empty when none), ordered by agent id then tool name.</summary>
    public IReadOnlyList<LoopLatchAgentState> Snapshot()
    {
        lock (_lock)
        {
            return _agents
                .SelectMany(agent => agent.Value.Latched.Select(l => new LoopLatchAgentState(agent.Key, l.Key, l.Value.FailureCount, l.Value.ErrorCode)))
                .OrderBy(s => s.AgentId, StringComparer.Ordinal)
                .ThenBy(s => s.Tool, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// The first diagnostic id (such as CS0103) in <paramref name="text"/>, only for a compile-gate rejection
    /// (<see cref="ToolErrorCode.ValidationFailed"/>); null for any other code or when the text has none.
    /// </summary>
    public static string? FirstDiagnosticId(string? errorCode, string? text)
    {
        if (errorCode != ToolErrorCode.ValidationFailed || string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = DiagnosticId.Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// The refusal text. With a next step: "{Tool} is DISABLED until another tool succeeds: it failed the same way {n} times. You MUST
    /// do the next step now: {next step, verbatim}". Without: the same lead, then "Read the current code first with ..." naming
    /// only the callable read tools other than the latched one, then "choose a different approach".
    /// </summary>
    public static string BuildRefusal(string toolName, int failureCount, string? nextStep, Func<string, bool>? isCallable = null)
    {
        var lead = $"{toolName}{RefusalMarker}: it failed the same way {failureCount} times. ";
        if (!string.IsNullOrWhiteSpace(nextStep))
        {
            return lead + NextStepDirective + nextStep;
        }

        var names = ReadTools.Where(t => t != toolName && (isCallable is null || isCallable(t))).ToList();
        var readFirst = names.Count switch
        {
            0 => "Read the current code first",
            1 => "Read the current code first with " + names[0],
            _ => "Read the current code first with " + string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1],
        };
        return lead + readFirst + ", then choose a different approach.";
    }

    /// <summary>
    /// True when <paramref name="resultJson"/> is this latch's refusal of <paramref name="toolName"/> (see <see cref="RefusalMessage"/>).
    /// </summary>
    public static bool IsRefusalBody(string toolName, string resultJson) => RefusalMessage(toolName, resultJson) is not null;

    /// <summary>
    /// The refusal message when <paramref name="resultJson"/> is this latch's refusal of <paramref name="toolName"/>: a failure
    /// body whose <c>errorData.errorCode</c> is <see cref="ToolErrorCode.TargetIneligible"/> and whose message starts with
    /// "{toolName}" + <see cref="RefusalMarker"/>. Null for anything else, including text that is not JSON. Used by the harness.
    /// </summary>
    public static string? RefusalMessage(string toolName, string resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errorData", out var errorData)
                && errorData.ValueKind == JsonValueKind.Object
                && errorData.TryGetProperty("errorCode", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == ToolErrorCode.TargetIneligible
                && errorData.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && (message.GetString() ?? "").StartsWith(toolName + RefusalMarker, StringComparison.Ordinal))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: not a refusal.
        }

        return null;
    }
}
