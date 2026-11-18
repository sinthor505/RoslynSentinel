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
        throw new NotImplementedException(); // TODO implement
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
        throw new NotImplementedException(); // TODO implement
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

    /// <summary>Assembles the transcript's settings block from what the client sent, what the operator declared, what the host reported and the model load config.</summary>
    public static RunSettings Build(double? sentTemperature, double? sentTopP, IReadOnlyDictionary<string, double> declaredSampler, SamplerSettings? reported, ModelLoadSettings? load)
    {
        throw new NotImplementedException(); // TODO implement
    }
}
