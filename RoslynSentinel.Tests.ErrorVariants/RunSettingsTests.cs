using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class RunSettingsTests
{
    private const string TargetKey = "qwen/target";

    private const string BaseUrl = "http://localhost:1234";

    private const string TwoModelsJson =
        "{\"models\":[" +
        "{\"key\":\"other/model\",\"loaded_instances\":[]}," +
        "{\"key\":\"qwen/target\",\"loaded_instances\":[{\"id\":\"qwen/target\",\"config\":" +
        "{\"context_length\":70144,\"flash_attention\":true,\"speculative_draft_mtp\":true,\"parallel\":1,\"eval_batch_size\":512}}]}" +
        "]}";

    private static readonly IReadOnlyDictionary<string, double> EmptyDeclared = new Dictionary<string, double>();

    [Test]
    public void FromReported_MapsNonNullArgumentsToVerifiedReportedValuesAndLeavesNullsNull()
    {
        var reported = SamplerSettings.FromReported(0.7, null, 0.5, null);

        Assert.That(reported.Temperature!.Value, Is.EqualTo(0.7));
        Assert.That(reported.Temperature!.Verified, Is.True);
        Assert.That(reported.Temperature!.Source, Is.EqualTo(SamplerValue.SourceReported));
        Assert.That(reported.TopP, Is.Null);
        Assert.That(reported.PresencePenalty!.Value, Is.EqualTo(0.5));
        Assert.That(reported.PresencePenalty!.Verified, Is.True);
        Assert.That(reported.FrequencyPenalty, Is.Null);
        Assert.That(reported.TopK, Is.Null);
        Assert.That(reported.MinP, Is.Null);
        Assert.That(reported.RepeatPenalty, Is.Null);
    }

    [Test]
    public void FromModelsJson_ReadsLoadConfigOfTheMatchingModelWhenItIsSecondListed()
    {
        var load = ModelLoadSettings.FromModelsJson(TwoModelsJson, TargetKey, BaseUrl);

        Assert.That(load.BaseUrl, Is.EqualTo(BaseUrl));
        Assert.That(load.ContextLength, Is.EqualTo(70144));
        Assert.That(load.FlashAttention, Is.True);
        Assert.That(load.SpeculativeDraftMtp, Is.True);
        Assert.That(load.Parallel, Is.EqualTo(1));
    }

    [Test]
    public void FromModelsJson_ModelNotListed_ReturnsBaseUrlOnly()
    {
        var load = ModelLoadSettings.FromModelsJson(TwoModelsJson, "missing/model", BaseUrl);

        AssertBaseUrlOnly(load);
    }

    [Test]
    public void FromModelsJson_InvalidJson_ReturnsBaseUrlOnly()
    {
        var load = ModelLoadSettings.FromModelsJson("{not json", TargetKey, BaseUrl);

        AssertBaseUrlOnly(load);
    }

    [Test]
    public void FromModelsJson_NullJson_ReturnsBaseUrlOnly()
    {
        var load = ModelLoadSettings.FromModelsJson(null, TargetKey, BaseUrl);

        AssertBaseUrlOnly(load);
    }

    [Test]
    public void Build_ReportedTemperatureEqualToSent_IsVerifiedReportedWithSentValue()
    {
        var reported = SamplerSettings.FromReported(0.7, null, null, null);

        var settings = RunSettings.Build(0.7, null, EmptyDeclared, reported, null);

        Assert.That(settings.Sampler.Temperature!.Value, Is.EqualTo(0.7));
        Assert.That(settings.Sampler.Temperature!.Verified, Is.True);
        Assert.That(settings.Sampler.Temperature!.Source, Is.EqualTo(SamplerValue.SourceReported));
        Assert.That(settings.Sampler.Temperature!.SentValue, Is.EqualTo(0.7));
    }

    [Test]
    public void Build_ReportedTopPDifferentFromSent_KeepsSentValue()
    {
        var reported = SamplerSettings.FromReported(null, 0.95, null, null);

        var settings = RunSettings.Build(null, 0.9, EmptyDeclared, reported, null);

        Assert.That(settings.Sampler.TopP!.Value, Is.EqualTo(0.95));
        Assert.That(settings.Sampler.TopP!.Verified, Is.True);
        Assert.That(settings.Sampler.TopP!.Source, Is.EqualTo(SamplerValue.SourceReported));
        Assert.That(settings.Sampler.TopP!.SentValue, Is.EqualTo(0.9));
    }

    [Test]
    public void Build_NothingReportedTemperatureSent_IsUnverifiedSent()
    {
        var settings = RunSettings.Build(0.8, null, EmptyDeclared, null, null);

        Assert.That(settings.Sampler.Temperature!.Value, Is.EqualTo(0.8));
        Assert.That(settings.Sampler.Temperature!.Verified, Is.False);
        Assert.That(settings.Sampler.Temperature!.Source, Is.EqualTo(SamplerValue.SourceSent));
        Assert.That(settings.Sampler.Temperature!.SentValue, Is.Null);
    }

    [Test]
    public void Build_NeitherReportedNorSent_TemperatureAndTopPAreNull()
    {
        var settings = RunSettings.Build(null, null, EmptyDeclared, null, null);

        Assert.That(settings.Sampler.Temperature, Is.Null);
        Assert.That(settings.Sampler.TopP, Is.Null);
    }

    [Test]
    public void Build_DeclaredValuesFillUnverifiedAndReportedPresencePenaltyBeatsDeclared()
    {
        var declared = new Dictionary<string, double>
        {
            ["topK"] = 40,
            ["minP"] = 0.05,
            ["repeatPenalty"] = 1.1,
            ["presencePenalty"] = 0.2,
            ["frequencyPenalty"] = 0.3,
        };
        var reported = SamplerSettings.FromReported(null, null, 0.7, null);

        var sampler = RunSettings.Build(null, null, declared, reported, null).Sampler;

        Assert.That(sampler.TopK!.Value, Is.EqualTo(40));
        Assert.That(sampler.TopK!.Verified, Is.False);
        Assert.That(sampler.TopK!.Source, Is.EqualTo(SamplerValue.SourceDeclared));
        Assert.That(sampler.MinP!.Value, Is.EqualTo(0.05));
        Assert.That(sampler.MinP!.Source, Is.EqualTo(SamplerValue.SourceDeclared));
        Assert.That(sampler.RepeatPenalty!.Value, Is.EqualTo(1.1));
        Assert.That(sampler.RepeatPenalty!.Source, Is.EqualTo(SamplerValue.SourceDeclared));
        Assert.That(sampler.PresencePenalty!.Value, Is.EqualTo(0.7));
        Assert.That(sampler.PresencePenalty!.Verified, Is.True);
        Assert.That(sampler.PresencePenalty!.Source, Is.EqualTo(SamplerValue.SourceReported));
        Assert.That(sampler.FrequencyPenalty!.Value, Is.EqualTo(0.3));
        Assert.That(sampler.FrequencyPenalty!.Verified, Is.False);
        Assert.That(sampler.FrequencyPenalty!.Source, Is.EqualTo(SamplerValue.SourceDeclared));
    }

    [Test]
    public void Serialize_OnlyTemperatureSet_OmitsTopKAndLoad()
    {
        var settings = RunSettings.Build(0.7, null, EmptyDeclared, null, null);

        var json = JsonSerializer.Serialize(settings);

        Assert.That(json, Does.Contain("Temperature"));
        Assert.That(json, Does.Not.Contain("TopK"));
        Assert.That(json, Does.Not.Contain("Load"));
    }

    private static void AssertBaseUrlOnly(ModelLoadSettings load)
    {
        Assert.That(load, Is.Not.Null);
        Assert.That(load.BaseUrl, Is.EqualTo(BaseUrl));
        Assert.That(load.ContextLength, Is.Null);
        Assert.That(load.FlashAttention, Is.Null);
        Assert.That(load.SpeculativeDraftMtp, Is.Null);
        Assert.That(load.Parallel, Is.Null);
    }
}
