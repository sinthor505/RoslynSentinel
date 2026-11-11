using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// One assist the loop-assist harness feature gave the model: a collapsed run of identical rejected
/// calls in the replayed history, or an injected fact-only message. Recorded on the transcript turn
/// that received it so a run's assists can be counted and located without text search.
/// </summary>
/// <param name="Kind"><see cref="CollapseKind"/>, <see cref="InjectionKind"/> or <see cref="LatchKind"/>; a string so the transcript stays readable and a new kind needs no converter.</param>
/// <param name="ToolName">The tool whose repeated call triggered the assist.</param>
/// <param name="RepeatCount">Injection: how many times the identical rejected call has been sent. Collapse: how many repeats were replaced by the note.</param>
/// <param name="Text">The injected message or the collapse note, exactly as sent to the model.</param>
public sealed record AgentAssist(string Kind, string ToolName, int RepeatCount, string Text)
{
    public const string CollapseKind = "collapse";
    public const string InjectionKind = "injection";

    /// <summary>A server loop-latch refusal (ToolLoopLatch): the call was not executed. RepeatCount counts this tool's refusals in the run so far; Text is the refusal message.</summary>
    public const string LatchKind = "latch";
}

/// <summary>
/// Switches for the loop-assist feature (collapse of repeated rejected calls in the replayed history, plus a
/// fact-only injection). Off unless <see cref="EnabledVariable"/> is truthy, so a clean baseline stays available.
/// </summary>
/// <param name="Enabled">Collapse and injection on.</param>
/// <param name="InjectionRole">"user" or "assistant": the role of the injected message and of the collapse note (the role is an open question to be tested).</param>
public sealed record LoopAssistOptions(bool Enabled, string InjectionRole)
{
    public const string EnabledVariable = "ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST";
    public const string RoleVariable = "ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST_ROLE";

    public static LoopAssistOptions Off { get; } = new(false, "user");

    /// <param name="readVariable">Environment reader; defaults to the process environment (a seam for tests).</param>
    public static LoopAssistOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= name => Environment.GetEnvironmentVariable(name);
        var enabled = IsTruthy(readVariable(EnabledVariable));
        var role = string.Equals(readVariable(RoleVariable)?.Trim(), "assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : "user";
        return new LoopAssistOptions(enabled, role);
    }

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}

/// <summary>
/// Per-run state of the loop-assist feature. Detects repeats (same tool + arguments, both rejected with the
/// same error text), builds the fact-only injection, and collapses repeats out of the history replayed to the
/// model. It never touches the raw message list or the transcript: the caller replays
/// <see cref="CollapseForReplay"/>'s result and records the returned <see cref="AgentAssist"/>s.
/// </summary>
public sealed class LoopAssistTracker(LoopAssistOptions options)
{
    // The server prepends this sentence from the 2nd identical ReplaceSnippet rejection on
    // (WorkspaceFileEditImpl.RepeatedRejectionNote and its two follow-ups), so the 1st and 2nd rejection texts
    // differ by it alone. Strip it before comparing or the 2nd call would never count as a repeat of the 1st.
    private static readonly Regex RepeatNote = new(
        @"You already sent this exact (?:edit|call) and it was rejected the same way; sending it again cannot succeed\.(?: Do what the next step below says instead\.| Change the edit to fix the errors below, or take a different approach\.)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string NextStepMarker = "Next step: ";

    private sealed class RepeatGroup(string toolName)
    {
        public string ToolName { get; } = toolName;
        public List<string> CallIds { get; } = [];
        public int ReportedCollapsed { get; set; }
    }

    private readonly Dictionary<string, RepeatGroup> _groups = new(StringComparer.Ordinal);

    public bool Enabled => options.Enabled;

    /// <summary>
    /// Records a finished tool call. Returns the injection assist when this call is the 2nd or later identical
    /// rejected call (null otherwise, and always null when the feature is off or the call succeeded). Repeats
    /// count across the whole run, consecutive or not.
    /// </summary>
    public AgentAssist? Observe(AgentToolCall call, string resultJson, bool isError)
    {
        if (!Enabled || !isError)
        {
            return null;
        }

        var errorText = ExtractErrorText(resultJson);
        var key = string.Join('\u0001', call.Name, CanonicalArguments(call.ArgumentsJson), NormalizeError(errorText));
        if (!_groups.TryGetValue(key, out var group))
        {
            _groups[key] = group = new RepeatGroup(call.Name);
        }

        group.CallIds.Add(call.Id);
        var repeatCount = group.CallIds.Count;
        if (repeatCount < 2)
        {
            return null;
        }

        return new AgentAssist(
            AgentAssist.InjectionKind, call.Name, repeatCount,
            BuildInjectionText(call.Name, repeatCount, ExtractNextStep(errorText)));
    }

    /// <summary>The message to append to the conversation for <paramref name="text"/>, in the configured role.</summary>
    public AgentChatMessage CreateAssistMessage(string text) =>
        new() { Role = options.InjectionRole, Content = text, IsLoopAssistNote = true };

    /// <summary>
    /// The history to replay: the first of each group of identical rejected calls stays with its result; later
    /// call/result pairs are removed and one note ("(sent N more times, rejected identically)") stands in at the
    /// position of the group's last repeat. An assistant turn keeps its reasoning and text, so the reasoning-replay
    /// cap counts the same turns. Returns <paramref name="messages"/> itself when nothing collapses.
    /// A collapse assist is appended to <paramref name="newAssists"/> only when a group grew since the last call, so an
    /// assist is one event per new repeat rather than one per turn.
    /// </summary>
    public IReadOnlyList<AgentChatMessage> CollapseForReplay(IReadOnlyList<AgentChatMessage> messages, List<AgentAssist> newAssists)
    {
        if (!Enabled)
        {
            return messages;
        }

        var removed = new Dictionary<string, RepeatGroup>(StringComparer.Ordinal);
        foreach (var group in _groups.Values.Where(g => g.CallIds.Count >= 2))
        {
            foreach (var id in group.CallIds.Skip(1))
            {
                removed[id] = group;
            }
        }

        if (removed.Count == 0)
        {
            return messages;
        }

        var result = new List<AgentChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && message.ToolCalls.Any(tc => removed.ContainsKey(tc.Id)))
            {
                result.Add(new AgentChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    ReasoningContent = message.ReasoningContent,
                    ToolCallId = message.ToolCallId,
                    TotalTokens = message.TotalTokens,
                    ToolCalls = message.ToolCalls.Where(tc => !removed.ContainsKey(tc.Id)).ToList(),
                    CollapsedToolCalls = true,
                });
            }
            else if (message.Role == "tool" && message.ToolCallId is not null && removed.TryGetValue(message.ToolCallId, out var group))
            {
                if (group.CallIds[^1] == message.ToolCallId)
                {
                    result.Add(CreateAssistMessage(CollapseNote(group.CallIds.Count - 1)));
                }
            }
            else
            {
                result.Add(message);
            }
        }

        foreach (var group in _groups.Values.Where(g => g.CallIds.Count - 1 > g.ReportedCollapsed))
        {
            group.ReportedCollapsed = group.CallIds.Count - 1;
            newAssists.Add(new AgentAssist(AgentAssist.CollapseKind, group.ToolName, group.ReportedCollapsed, CollapseNote(group.ReportedCollapsed)));
        }

        return result;
    }

    /// <summary>
    /// The agent.log run-summary sentence. "passed" means the model ended the loop on its own, not that the task
    /// was done correctly (the test's own assertions decide that).
    /// </summary>
    public static string DescribeRunOutcome(bool converged, int assistCount) =>
        $"Run {(converged ? "passed" : "failed")} {(assistCount == 0 ? "clean" : $"with {assistCount} assist{(assistCount == 1 ? "" : "s")}")}.";

    internal static string CollapseNote(int moreTimes) =>
        $"(sent {moreTimes} more time{(moreTimes == 1 ? "" : "s")}, rejected identically)";

    /// <summary>
    /// First person, facts only. The wording rotates with the count (2nd; odd later; even later) so two
    /// consecutive injections are never the same sentence.
    /// </summary>
    internal static string BuildInjectionText(string toolName, int repeatCount, string? nextStep)
    {
        var step = nextStep is null ? null : StripPlaceholderReason(nextStep);
        var next = string.IsNullOrWhiteSpace(step) ? "" : $" The error's next step says: {WithSentenceEnd(step)}";
        if (repeatCount <= 2)
        {
            return $"I have sent the same {toolName} call {repeatCount} times and it was rejected the same way each time.{next} My next call must be a different tool.";
        }

        return repeatCount % 2 == 1
            ? $"This is the {Ordinal(repeatCount)} time I have sent the same {toolName} call, and it was rejected the same way every time.{next} I must not send it again; my next call must be a different tool."
            : $"For the {Ordinal(repeatCount)} time I have sent the same {toolName} call and got the same rejection.{next} Repeating it cannot work, so my next call must be a different tool.";
    }

    /// <summary>The text after "Next step: " to the end of its line, verbatim; null when the error has none.</summary>
    internal static string? ExtractNextStep(string errorText)
    {
        var start = errorText.IndexOf(NextStepMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += NextStepMarker.Length;
        var end = errorText.IndexOfAny(['\r', '\n'], start);
        var step = (end < 0 ? errorText[start..] : errorText[start..end]).Trim();
        return step.Length == 0 ? null : step;
    }

    private static string WithSentenceEnd(string text) => text[^1] is '.' or '!' or '?' ? text : text + ".";

    /// <summary>
    /// Drops the server's placeholder reason argument from a quoted next step. The error text quotes an example
    /// call, and models copy the placeholder into their next call; reason is not part of the edit, so it goes.
    /// </summary>
    internal static string StripPlaceholderReason(string step)
    {
        const string followed = "reason: \"<why you call this>\", ";
        const string preceded = ", reason: \"<why you call this>\"";
        const string only = "reason: \"<why you call this>\"";
        return step.Replace(followed, "", StringComparison.Ordinal)
            .Replace(preceded, "", StringComparison.Ordinal)
            .Replace(only, "", StringComparison.Ordinal);
    }

    private static string Ordinal(int n) =>
        n % 100 is >= 11 and <= 13
            ? n + "th"
            : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };

    /// <summary>The server's error message, not the whole result: the result envelope carries a per-call toolCallId.</summary>
    private static string ExtractErrorText(string resultJson)
    {
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errorData", out var errorData)
                        && errorData.ValueKind == JsonValueKind.Object
                        && errorData.TryGetProperty("message", out var dataMessage)
                        && dataMessage.ValueKind == JsonValueKind.String)
                    {
                        return dataMessage.GetString() ?? "";
                    }

                    foreach (var name in new[] { "error", "message" })
                    {
                        if (root.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            return text.GetString() ?? "";
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON (a plain-text error body): compare the text as is.
            }
        }

        return resultJson ?? "";
    }

    private static string NormalizeError(string errorText) =>
        Regex.Replace(RepeatNote.Replace(errorText, ""), @"\s+", " ").Trim();

    private static string CanonicalArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "";
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is JsonObject arguments)
            {
                arguments.Remove("reason");
                if (arguments["batchEdits"] is JsonArray batch)
                {
                    foreach (var item in batch.OfType<JsonObject>())
                    {
                        item.Remove("reason");
                    }
                }
            }

            return node?.ToJsonString() ?? argumentsJson.Trim();
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }
    }
}
