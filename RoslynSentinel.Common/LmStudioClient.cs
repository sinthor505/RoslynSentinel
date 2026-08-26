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

    // Cached for the process lifetime after the first successful lookup: LM Studio's context size
    // for a given loaded model doesn't change without a restart, so re-querying /api/v0/models on
    // every CompleteAsync call would just be wasted latency. 0 means "not yet resolved"; -1 means
    // "resolution failed, don't keep retrying" (falls back to skipping the fail-fast check).
    private int _cachedContextLength;
    private readonly SemaphoreSlim _contextLengthLock = new(1, 1);

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
        await EnsureFitsContextAsync(systemPrompt, userPrompt, maxTokens, cancellationToken);

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

    // Fails fast on the client before an oversized request reaches LM Studio and burns time on a
    // 400 round-trip (llama.cpp's engine rejects the whole request rather than truncating it — see
    // "exceed_context_size_error"). Token counts are estimated, not exact (see PromptTokenEstimator),
    // so this is a best-effort guard, not a guarantee; LM Studio's own check remains authoritative.
    private async Task EnsureFitsContextAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken)
    {
        var contextLength = await GetContextLengthAsync(cancellationToken);
        if (contextLength <= 0)
        {
            return;
        }

        var estimatedPromptTokens = PromptTokenEstimator.EstimateTokens(_model, systemPrompt)
            + PromptTokenEstimator.EstimateTokens(_model, userPrompt);
        var estimatedTotal = estimatedPromptTokens + maxTokens;

        if (estimatedTotal > contextLength)
        {
            throw new InvalidOperationException(
                $"Prompt too large for '{_model}': estimated {estimatedPromptTokens} prompt tokens + {maxTokens} max completion tokens "
                + $"= ~{estimatedTotal} tokens, exceeding the model's {contextLength}-token context window. "
                + "Reduce the input size or lower --llm-max-tokens.");
        }
    }

    // Resolves and caches the loaded model's context window via LM Studio's /api/v0/models endpoint
    // (distinct from the OpenAI-compatible /v1 surface this client otherwise uses). Resolution
    // failures (server unreachable, model not found, field missing) are cached too, as -1, so a
    // down LM Studio server doesn't add a failed HTTP call to every single completion request.
    private async Task<int> GetContextLengthAsync(CancellationToken cancellationToken)
    {
        if (_cachedContextLength != 0)
        {
            return _cachedContextLength;
        }

        await _contextLengthLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedContextLength != 0)
            {
                return _cachedContextLength;
            }

            _cachedContextLength = await FetchContextLengthAsync(cancellationToken);
            return _cachedContextLength;
        }
        finally
        {
            _contextLengthLock.Release();
        }
    }

    private async Task<int> FetchContextLengthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modelsUrl = new Uri(_httpClient.BaseAddress!, "../api/v0/models");
            using var response = await _httpClient.GetAsync(modelsUrl, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Could not fetch context size from LM Studio ({StatusCode}); skipping client-side context-size checks. Body: {Body}",
                    response.StatusCode, responseText);
                return -1;
            }

            var parsed = JsonSerializer.Deserialize<LmStudioModelsResponse>(responseText, _jsonOptions);
            var match = parsed?.Data?.FirstOrDefault(m => string.Equals(m.Id, _model, StringComparison.Ordinal));
            if (match?.LoadedContextLength is int contextLength and > 0)
            {
                return contextLength;
            }

            _logger.LogWarning(
                "LM Studio's /api/v0/models response had no loaded_context_length for model '{Model}'; skipping client-side context-size checks.",
                _model);
            return -1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch context size from LM Studio; skipping client-side context-size checks.");
            return -1;
        }
    }

    private sealed class LmStudioModelsResponse
    {
        public List<LmStudioModelInfo>? Data { get; set; }
    }

    private sealed class LmStudioModelInfo
    {
        public string? Id { get; set; }
        [JsonPropertyName("loaded_context_length")]
        public int? LoadedContextLength { get; set; }
        [JsonPropertyName("max_context_length")]
        public int? MaxContextLength { get; set; }
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
