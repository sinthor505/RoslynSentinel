using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// <see cref="ILlmClient"/> implementation talking to a locally-hosted LM Studio server over its
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint. Single-turn, no tool use.
/// </summary>
public sealed class LmStudioClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LmStudioClient> _logger;
    private readonly string _model;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LmStudioClient(HttpClient httpClient, ILogger<LmStudioClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _model = LlmOptions.Model
            ?? throw new InvalidOperationException(
                "The LLM model must be set via --llm-model or the ROSLYNSENTINEL_LLM_MODEL environment variable (LM Studio needs the loaded model's name).");
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default)
    {
        var requestBody = new ChatCompletionRequest
        {
            Model = _model,
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            ],
            MaxTokens = maxTokens,
            Stream = false,
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(requestBody, _jsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("LM Studio request failed: {StatusCode} {Body}", response.StatusCode, responseText);
            throw new InvalidOperationException($"LM Studio request failed with {response.StatusCode}: {responseText}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, _jsonOptions);
        var completion = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(completion))
        {
            throw new InvalidOperationException("LM Studio returned an empty completion.");
        }

        return completion.Trim();
    }

    private sealed class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = [];
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }
        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
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
