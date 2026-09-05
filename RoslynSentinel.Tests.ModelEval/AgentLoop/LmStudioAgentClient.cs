using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tests.ModelEval.AgentLoop;

/// <summary>
/// Multi-turn, tool-calling LM Studio client for the model-eval harness. Distinct from
/// <see cref="ILlmClient"/>/<c>LmStudioClient</c> (single-turn, plain text, used by BulkComment) —
/// this client sends the full running message history plus an OpenAI-style <c>tools</c> array on
/// every call and returns whatever the model responded with (prose, tool calls, or both), leaving
/// all looping/dispatch to <see cref="ModelAgentRunner"/>.
///
/// Talks to the OpenAI-compatible <c>/v1/responses</c> endpoint (not <c>/v1/chat/completions</c>)
/// with <c>stream: true</c>, so it works against any Responses-API-compatible server, not just LM
/// Studio. Streaming gives two things <c>/v1/chat/completions</c> doesn't: reasoning content
/// surfaced as its own typed output item (rather than mixed into or absent from the message text),
/// and live per-turn progress logged as tokens arrive instead of one silent block until the whole
/// turn completes.
/// </summary>
public sealed class LmStudioAgentClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LmStudioAgentClient> _logger;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public LmStudioAgentClient(HttpClient httpClient, ILogger<LmStudioAgentClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _model = LlmOptions.Model
            ?? throw new InvalidOperationException(
                "The LLM model must be set via --llm-model or the ROSLYNSENTINEL_LLM_MODEL environment variable (LM Studio needs the loaded model's name).");
    }

    public async Task<AgentChatMessage> CompleteAsync(
        IReadOnlyList<AgentChatMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new ResponsesRequest
        {
            Model = _model,
            Input = messages.SelectMany(ToInputItems).ToList(),
            Tools = tools.Count > 0 ? tools.Select(ToWireTool).ToList() : null,
            ToolChoice = tools.Count > 0 ? "auto" : null,
            MaxOutputTokens = maxTokens,
            Stream = true,
            Temperature = LlmOptions.Temperature,
            TopP = LlmOptions.TopP,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("LM Studio request failed: {StatusCode} {Body}", response.StatusCode, errorBody);
            throw new InvalidOperationException($"LM Studio request failed with {response.StatusCode}: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        ResponseObject? completedResponse = null;
        var reasoningBuilders = new Dictionary<string, StringBuilder>();
        var messageBuilders = new Dictionary<string, StringBuilder>();

        await foreach (var (eventType, data) in ReadServerSentEventsAsync(reader, cancellationToken))
        {
            switch (eventType)
            {
                case "response.reasoning_text.delta":
                {
                    var evt = JsonSerializer.Deserialize<ReasoningTextDeltaEvent>(data, JsonOptions)!;
                    var sb = reasoningBuilders.TryGetValue(evt.ItemId, out var existing)
                        ? existing
                        : reasoningBuilders[evt.ItemId] = new StringBuilder();
                    sb.Append(evt.Delta);
                    break;
                }
                case "response.output_text.delta":
                {
                    var evt = JsonSerializer.Deserialize<OutputTextDeltaEvent>(data, JsonOptions)!;
                    var sb = messageBuilders.TryGetValue(evt.ItemId, out var existing)
                        ? existing
                        : messageBuilders[evt.ItemId] = new StringBuilder();
                    sb.Append(evt.Delta);
                    _logger.LogInformation("LM Studio streaming: {Delta}", evt.Delta);
                    break;
                }
                case "response.output_item.done":
                {
                    var evt = JsonSerializer.Deserialize<OutputItemDoneEvent>(data, JsonOptions)!;
                    if (evt.Item.Type == "function_call")
                    {
                        _logger.LogInformation(
                            "LM Studio streaming: tool call {Tool}({Args})", evt.Item.Name, evt.Item.Arguments);
                    }
                    break;
                }
                case "response.completed":
                {
                    var evt = JsonSerializer.Deserialize<ResponseCompletedEvent>(data, JsonOptions)!;
                    completedResponse = evt.Response;
                    break;
                }
                case "response.failed":
                case "error":
                {
                    _logger.LogWarning("LM Studio streaming error event: {Data}", data);
                    throw new InvalidOperationException($"LM Studio returned a streaming error event: {data}");
                }
            }
        }

        if (completedResponse is null)
        {
            throw new InvalidOperationException("LM Studio stream ended without a response.completed event.");
        }

        // LM Studio always echoes back whatever value it actually used, including its own default
        // when the request omitted the field — so a mismatch against a value we explicitly sent
        // means LM Studio silently coerced or ignored it (seen for temperature/top_p with certain
        // presets — see LM Studio bug tracker #1389), which is worth surfacing loudly rather than
        // silently trusting the request body we sent.
        if (LlmOptions.Temperature is { } requestedTemperature &&
            completedResponse.Temperature is { } actualTemperature &&
            Math.Abs(requestedTemperature - actualTemperature) > 0.0001)
        {
            _logger.LogWarning(
                "Requested temperature {Requested} but LM Studio reports it used {Actual} — a preset or " +
                "server-side override may be silently taking precedence over the request body.",
                requestedTemperature, actualTemperature);
        }
        if (LlmOptions.TopP is { } requestedTopP &&
            completedResponse.TopP is { } actualTopP &&
            Math.Abs(requestedTopP - actualTopP) > 0.0001)
        {
            _logger.LogWarning(
                "Requested top_p {Requested} but LM Studio reports it used {Actual} — a preset or " +
                "server-side override may be silently taking precedence over the request body.",
                requestedTopP, actualTopP);
        }

        var reasoningText = string.Concat(reasoningBuilders.Values.Select(sb => sb.ToString()));
        var messageText = completedResponse.Output
            .Where(o => o.Type == "message")
            .SelectMany(o => o.Content ?? [])
            .Where(c => c.Type == "output_text")
            .Select(c => c.Text)
            .FirstOrDefault();

        var toolCalls = completedResponse.Output
            .Where(o => o.Type == "function_call")
            .Select(o => new AgentToolCall
            {
                Id = o.CallId ?? "",
                Name = o.Name ?? "",
                ArgumentsJson = o.Arguments ?? "{}",
            })
            .ToList();

        return new AgentChatMessage
        {
            Role = "assistant",
            Content = messageText,
            ReasoningContent = string.IsNullOrEmpty(reasoningText) ? null : reasoningText,
            ToolCalls = toolCalls,
        };
    }

    /// <summary>Parses a raw SSE byte stream into (event, data) pairs. LM Studio sends one "event: &lt;type&gt;" line followed by one "data: &lt;json&gt;" line per message, separated by a blank line.</summary>
    private static async IAsyncEnumerable<(string EventType, string Data)> ReadServerSentEventsAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventType = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventType is not null)
            {
                yield return (eventType, line["data: ".Length..]);
                eventType = null;
            }
        }
    }

    private static List<object> ToInputItems(AgentChatMessage message)
    {
        if (message.Role == "tool")
        {
            return
            [
                new FunctionCallOutputItem
                {
                    CallId = message.ToolCallId ?? "",
                    Output = message.Content ?? "",
                },
            ];
        }

        if (message.Role == "assistant" && message.ToolCalls.Count > 0)
        {
            return message.ToolCalls
                .Select(tc => (object)new FunctionCallItem
                {
                    CallId = tc.Id,
                    Name = tc.Name,
                    Arguments = tc.ArgumentsJson,
                })
                .ToList();
        }

        return [new InputMessageItem { Role = message.Role, Content = message.Content ?? "" }];
    }

    private static ResponsesTool ToWireTool(AgentToolDefinition tool) => new()
    {
        Type = "function",
        Name = tool.Name,
        Description = tool.Description,
        Parameters = tool.ParametersSchema,
    };

    private sealed class ResponsesRequest
    {
        public string Model { get; set; } = "";
        public List<object> Input { get; set; } = [];
        public List<ResponsesTool>? Tools { get; set; }
        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; set; }
        [JsonPropertyName("max_output_tokens")]
        public int MaxOutputTokens { get; set; }
        public bool Stream { get; set; }

        // Both omitted (null) unless LlmOptions.Temperature/TopP is explicitly set — confirmed
        // 2026-09-05 that LM Studio's /v1/responses echoes these back verbatim in
        // ResponseObject.Temperature/TopP when sent, unlike top_k/repeat_penalty/min_p (documented
        // for /v1/chat/completions but absent from /v1/responses' response body AND confirmed via
        // an A/B behavioral test to have zero effect on generation there) — those three are
        // deliberately NOT wired here; see LlmOptions.TopK's remarks.
        public double? Temperature { get; set; }
        [JsonPropertyName("top_p")]
        public double? TopP { get; set; }
    }

    private sealed class InputMessageItem
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class FunctionCallItem
    {
        public string Type { get; set; } = "function_call";
        [JsonPropertyName("call_id")]
        public string CallId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private sealed class FunctionCallOutputItem
    {
        public string Type { get; set; } = "function_call_output";
        [JsonPropertyName("call_id")]
        public string CallId { get; set; } = "";
        public string Output { get; set; } = "";
    }

    private sealed class ResponsesTool
    {
        public string Type { get; set; } = "function";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public JsonElement Parameters { get; set; }
    }

    private sealed class ReasoningTextDeltaEvent
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = "";
        public string Delta { get; set; } = "";
    }

    private sealed class OutputTextDeltaEvent
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = "";
        public string Delta { get; set; } = "";
    }

    private sealed class OutputItemDoneEvent
    {
        public OutputItem Item { get; set; } = new();
    }

    private sealed class ResponseCompletedEvent
    {
        public ResponseObject Response { get; set; } = new();
    }

    private sealed class ResponseObject
    {
        public List<OutputItem> Output { get; set; } = [];

        // Only meaningful for verifying LlmOptions.Temperature/TopP were actually honored (see
        // CompleteAsync's post-response check) — LM Studio always echoes SOME value here (its own
        // default when the request omitted the field), not just when we sent one.
        public double? Temperature { get; set; }
        [JsonPropertyName("top_p")]
        public double? TopP { get; set; }
    }

    private sealed class OutputItem
    {
        public string Type { get; set; } = "";
        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string? Arguments { get; set; }
        public List<OutputContentPart>? Content { get; set; }
    }

    private sealed class OutputContentPart
    {
        public string Type { get; set; } = "";
        public string Text { get; set; } = "";
    }
}

/// <summary>One message in the running conversation the harness maintains itself (not tied to the wire format).</summary>
public sealed class AgentChatMessage
{
    public required string Role { get; init; } // "system" | "user" | "assistant" | "tool"
    public string? Content { get; init; }
    /// <summary>The model's reasoning/thinking text for this turn, when the backend streams it as a separate channel (e.g. Responses API's "reasoning" output item). Null for roles that never carry it (user/tool) or when the model/backend didn't produce any.</summary>
    public string? ReasoningContent { get; init; }
    public string? ToolCallId { get; init; } // set only on role:"tool" messages
    public List<AgentToolCall> ToolCalls { get; init; } = [];
}

public sealed class AgentToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}

/// <summary>An MCP tool translated into the shape LM Studio's OpenAI-compatible endpoint expects.</summary>
public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required JsonElement ParametersSchema { get; init; }
}
