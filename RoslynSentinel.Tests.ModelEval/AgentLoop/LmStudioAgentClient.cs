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
        var requestBody = new ChatCompletionRequest
        {
            Model = _model,
            Messages = messages.Select(ToWireMessage).ToList(),
            Tools = tools.Count > 0 ? tools.Select(ToWireTool).ToList() : null,
            ToolChoice = tools.Count > 0 ? "auto" : null,
            MaxTokens = maxTokens,
            Stream = false,
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("LM Studio request failed: {StatusCode} {Body}", response.StatusCode, responseText);
            throw new InvalidOperationException($"LM Studio request failed with {response.StatusCode}: {responseText}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions);
        var message = parsed?.Choices?.FirstOrDefault()?.Message;
        if (message is null)
        {
            throw new InvalidOperationException($"LM Studio returned no completion choices. Raw body: {responseText}");
        }

        return new AgentChatMessage
        {
            Role = "assistant",
            Content = message.Content,
            ToolCalls = message.ToolCalls?.Select(tc => new AgentToolCall
            {
                Id = tc.Id ?? "",
                Name = tc.Function?.Name ?? "",
                ArgumentsJson = tc.Function?.Arguments ?? "{}",
            }).ToList() ?? [],
        };
    }

    private static ChatMessage ToWireMessage(AgentChatMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
        ToolCallId = message.ToolCallId,
        ToolCalls = message.ToolCalls.Count > 0
            ? message.ToolCalls.Select(tc => new ChatToolCall
            {
                Id = tc.Id,
                Type = "function",
                Function = new ChatFunctionCall { Name = tc.Name, Arguments = tc.ArgumentsJson },
            }).ToList()
            : null,
    };

    private static ChatTool ToWireTool(AgentToolDefinition tool) => new()
    {
        Type = "function",
        Function = new ChatFunctionDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = tool.ParametersSchema,
        },
    };

    private sealed class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = [];
        public List<ChatTool>? Tools { get; set; }
        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; set; }
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }
        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        public string Role { get; set; } = "";
        public string? Content { get; set; }
        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }
        [JsonPropertyName("tool_calls")]
        public List<ChatToolCall>? ToolCalls { get; set; }
    }

    private sealed class ChatTool
    {
        public string Type { get; set; } = "function";
        public ChatFunctionDefinition Function { get; set; } = new();
    }

    private sealed class ChatFunctionDefinition
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public JsonElement Parameters { get; set; }
    }

    private sealed class ChatToolCall
    {
        public string? Id { get; set; }
        public string Type { get; set; } = "function";
        public ChatFunctionCall? Function { get; set; }
    }

    private sealed class ChatFunctionCall
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    private sealed class ChatCompletionResponse
    {
        public List<ChatCompletionChoice>? Choices { get; set; }
    }

    private sealed class ChatCompletionChoice
    {
        public ChatMessage? Message { get; set; }
    }
}

/// <summary>One message in the running conversation the harness maintains itself (not tied to the wire format).</summary>
public sealed class AgentChatMessage
{
    public required string Role { get; init; } // "system" | "user" | "assistant" | "tool"
    public string? Content { get; init; }
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
