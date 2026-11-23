using System.Text.Json.Serialization;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// One sampler value as the run settings block records it, with where the value came from.
/// </summary>
public sealed class SamplerValue
{
    /// <summary>The client sent the value; the model host did not echo it back.</summary>
    public const string SourceSent = "sent";

    /// <summary>The model host echoed the value back in its response.</summary>
    public const string SourceReported = "reported";

    /// <summary>The operator stated the value; it is not verifiable from responses.</summary>
    public const string SourceDeclared = "declared";

    /// <summary>The sampler value.</summary>
    public double Value
    {
        get; init;
    }

    /// <summary>True only when the value was echoed back by the model host.</summary>
    public bool Verified
    {
        get; init;
    }

    /// <summary>One of the SourceSent, SourceReported or SourceDeclared constants.</summary>
    public required string Source
    {
        get; init;
    }

    /// <summary>For a reported value, what the client put in the request; null when nothing was sent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SentValue
    {
        get; init;
    }
}

/// <summary>
/// The sampler values a run used, each with its verification state.
/// </summary>
public sealed class SamplerSettings
{
    /// <summary>Sampling temperature.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? Temperature
    {
        get; init;
    }

    /// <summary>Nucleus sampling top-p.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? TopP
    {
        get; init;
    }

    /// <summary>Presence penalty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? PresencePenalty
    {
        get; init;
    }

    /// <summary>Frequency penalty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? FrequencyPenalty
    {
        get; init;
    }

    /// <summary>Top-k sampling.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? TopK
    {
        get; init;
    }

    /// <summary>Min-p sampling.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? MinP
    {
        get; init;
    }

    /// <summary>Repeat penalty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplerValue? RepeatPenalty
    {
        get; init;
    }

    /// <summary>A SamplerSettings holding the values the host echoed in a response; Verified=true, Source=reported; a null argument leaves that member null.</summary>
    public static SamplerSettings FromReported(double? temperature, double? topP, double? presencePenalty, double? frequencyPenalty)
    {
        return new SamplerSettings
        {
            Temperature = Reported(temperature),
            TopP = Reported(topP),
            PresencePenalty = Reported(presencePenalty),
            FrequencyPenalty = Reported(frequencyPenalty),
        };

        static SamplerValue? Reported(double? value) =>
            value is null ? null : new SamplerValue { Value = value.Value, Verified = true, Source = SamplerValue.SourceReported };
    }
}

/// <summary>
/// Model-load configuration of the loaded model instance a run used.
/// </summary>
public sealed class ModelLoadSettings
{
    /// <summary>LM Studio host base URL the run used.</summary>
    public required string BaseUrl
    {
        get; init;
    }

    /// <summary>Loaded context length in tokens.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength
    {
        get; init;
    }

    /// <summary>Whether flash attention was enabled at load time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FlashAttention
    {
        get; init;
    }

    /// <summary>Whether speculative draft MTP was enabled at load time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SpeculativeDraftMtp
    {
        get; init;
    }

    /// <summary>Number of parallel slots configured at load time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Parallel
    {
        get; init;
    }

    /// <summary>Reads the loaded instance's config of the model whose key equals modelKey from an LM Studio GET /api/v1/models body; never null; only BaseUrl is set when the json is null, invalid, or lacks that model or a loaded instance.</summary>
    public static ModelLoadSettings FromModelsJson(string? modelsJson, string modelKey, string baseUrl)
    {
        var empty = new ModelLoadSettings { BaseUrl = baseUrl };
        if (string.IsNullOrWhiteSpace(modelsJson))
        {
            return empty;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(modelsJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("models", out var models)
                || models.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return empty;
            }

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !model.TryGetProperty("key", out var key)
                    || key.ValueKind != System.Text.Json.JsonValueKind.String
                    || key.GetString() != modelKey)
                {
                    continue;
                }

                var config = FindLoadedConfig(model, modelKey);
                return config is { } c ? ReadConfig(c, baseUrl) : empty;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return empty;
        }

        return empty;

        static System.Text.Json.JsonElement? FindLoadedConfig(System.Text.Json.JsonElement model, string modelKey)
        {
            if (!model.TryGetProperty("loaded_instances", out var instances) || instances.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            System.Text.Json.JsonElement? fallback = null;
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !instance.TryGetProperty("config", out var config)
                    || config.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                if (instance.TryGetProperty("id", out var id)
                    && id.ValueKind == System.Text.Json.JsonValueKind.String
                    && id.GetString() == modelKey)
                {
                    return config;
                }

                fallback ??= config;
            }

            return fallback;
        }

        static ModelLoadSettings ReadConfig(System.Text.Json.JsonElement config, string baseUrl)
        {
            return new ModelLoadSettings
            {
                BaseUrl = baseUrl,
                ContextLength = TryInt(config, "context_length"),
                FlashAttention = TryBool(config, "flash_attention"),
                SpeculativeDraftMtp = TryBool(config, "speculative_draft_mtp"),
                Parallel = TryInt(config, "parallel"),
            };

            static int? TryInt(System.Text.Json.JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

            static bool? TryBool(System.Text.Json.JsonElement element, string name) =>
                element.TryGetProperty(name, out var value)
                    && (value.ValueKind == System.Text.Json.JsonValueKind.True || value.ValueKind == System.Text.Json.JsonValueKind.False)
                    ? value.GetBoolean()
                    : null;
        }
    }
}

/// <summary>
/// The settings block written to the transcript: sampler values and model-load configuration for one run.
/// </summary>
public sealed class RunSettings
{
    /// <summary>Sampler values for the run, each with its source.</summary>
    public required SamplerSettings Sampler
    {
        get; init;
    }

    /// <summary>Model-load configuration, or null when it could not be recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelLoadSettings? Load
    {
        get; init;
    }

    /// <summary>True when the operator passed the accept flag, acknowledging the declared sampler values (Source declared) cannot be verified from the host's responses; omitted from the JSON when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AcceptedUnverifiedSampler
    {
        get; init;
    }

    /// <summary>Assembles the transcript's settings block from what the client sent, what the operator declared, what the host reported, the model load config and whether the operator accepted unverified sampler values.</summary>
    public static RunSettings Build(double? sentTemperature, double? sentTopP, IReadOnlyDictionary<string, double> declaredSampler, SamplerSettings? reported, ModelLoadSettings? load, bool acceptedUnverifiedSampler = false)
    {
        return new RunSettings
        {
            Sampler = new SamplerSettings
            {
                Temperature = SentOrReported(reported?.Temperature, sentTemperature),
                TopP = SentOrReported(reported?.TopP, sentTopP),
                PresencePenalty = reported?.PresencePenalty ?? DeclaredValue(declaredSampler, "presencePenalty"),
                FrequencyPenalty = reported?.FrequencyPenalty ?? DeclaredValue(declaredSampler, "frequencyPenalty"),
                TopK = DeclaredValue(declaredSampler, "topK"),
                MinP = DeclaredValue(declaredSampler, "minP"),
                RepeatPenalty = DeclaredValue(declaredSampler, "repeatPenalty"),
            },
            Load = load,
            AcceptedUnverifiedSampler = acceptedUnverifiedSampler,
        };

        static SamplerValue? SentOrReported(SamplerValue? reportedValue, double? sent)
        {
            if (reportedValue is not null)
            {
                return new SamplerValue
                {
                    Value = reportedValue.Value,
                    Verified = true,
                    Source = SamplerValue.SourceReported,
                    SentValue = sent,
                };
            }

            if (sent is not null)
            {
                return new SamplerValue { Value = sent.Value, Verified = false, Source = SamplerValue.SourceSent };
            }

            return null;
        }

        static SamplerValue? DeclaredValue(IReadOnlyDictionary<string, double> declared, string key) =>
            declared.TryGetValue(key, out var value)
                ? new SamplerValue { Value = value, Verified = false, Source = SamplerValue.SourceDeclared }
                : null;
    }
}
