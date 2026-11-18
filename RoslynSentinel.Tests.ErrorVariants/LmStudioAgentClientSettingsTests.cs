using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// The client's ReportedSampler (the first response's echoed values wins) and its cached load settings,
/// against a scripted model host; no LM Studio needed.
/// </summary>
[TestFixture]
// LlmOptions.Configure is global state, so these fixtures must not run in parallel with each other.
[NonParallelizable]
public class LmStudioAgentClientSettingsTests
{
    [SetUp]
    public void SetUp()
    {
        LlmOptions.Configure([]);
    }

    [TearDown]
    public void TearDown()
    {
        LlmOptions.Configure([]);
    }

    [Test]
    public void ReportedSampler_IsNullBeforeAnyCompletion()
    {
        var client = NewClient(new ScriptedModelHandler());

        Assert.That(client.ReportedSampler, Is.Null);
    }

    [Test]
    public async Task ReportedSampler_HoldsTheEchoedValuesOfTheFirstResponse()
    {
        var client = NewClient(new ScriptedModelHandler(
            ScriptedModelHandler.FinalMessageSse("done", 0.7, 0.9, 0.1, 0.2)));

        await CompleteOnceAsync(client);

        var sampler = client.ReportedSampler;
        Assert.That(sampler, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sampler!.Temperature!.Value, Is.EqualTo(0.7));
            Assert.That(sampler.Temperature!.Verified, Is.True);
            Assert.That(sampler.Temperature!.Source, Is.EqualTo("reported"));
            Assert.That(sampler.TopP!.Value, Is.EqualTo(0.9));
            Assert.That(sampler.TopP!.Verified, Is.True);
            Assert.That(sampler.TopP!.Source, Is.EqualTo("reported"));
            Assert.That(sampler.PresencePenalty!.Value, Is.EqualTo(0.1));
            Assert.That(sampler.PresencePenalty!.Verified, Is.True);
            Assert.That(sampler.PresencePenalty!.Source, Is.EqualTo("reported"));
            Assert.That(sampler.FrequencyPenalty!.Value, Is.EqualTo(0.2));
            Assert.That(sampler.FrequencyPenalty!.Verified, Is.True);
            Assert.That(sampler.FrequencyPenalty!.Source, Is.EqualTo("reported"));
            Assert.That(sampler.TopK, Is.Null);
            Assert.That(sampler.MinP, Is.Null);
            Assert.That(sampler.RepeatPenalty, Is.Null);
        });
    }

    [Test]
    public async Task ReportedSampler_IsNotReplacedByALaterResponse()
    {
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FinalMessageSse("first", 0.7, 0.9, 0.1, 0.2),
            ScriptedModelHandler.FinalMessageSse("second", 0.3, 0.4, 0.5, 0.6));
        var client = NewClient(model);

        await CompleteOnceAsync(client);
        await CompleteOnceAsync(client);

        Assert.That(model.Requests, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(client.ReportedSampler!.Temperature!.Value, Is.EqualTo(0.7));
            Assert.That(client.ReportedSampler!.FrequencyPenalty!.Value, Is.EqualTo(0.2));
        });
    }

    [Test]
    public async Task ReportedSampler_StaysNullUntilAResponseEchoesOne()
    {
        var client = NewClient(new ScriptedModelHandler(
            ScriptedModelHandler.FinalMessageSse("silent"),
            ScriptedModelHandler.FinalMessageSse("echoing", 0.5)));

        await CompleteOnceAsync(client);
        Assert.That(client.ReportedSampler, Is.Null);

        await CompleteOnceAsync(client);
        Assert.Multiple(() =>
        {
            Assert.That(client.ReportedSampler!.Temperature!.Value, Is.EqualTo(0.5));
            Assert.That(client.ReportedSampler!.TopP, Is.Null);
        });
    }

    [Test]
    public async Task GetLoadSettingsAsync_ReadsTheLoadedInstanceConfig()
    {
        var client = NewClient(new ScriptedModelHandler
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJsonWithConfig("settings-test-model", 70144, true, true, 1),
        });

        var settings = await client.GetLoadSettingsAsync(TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(settings.ContextLength, Is.EqualTo(70144));
            Assert.That(settings.FlashAttention, Is.True);
            Assert.That(settings.SpeculativeDraftMtp, Is.True);
            Assert.That(settings.Parallel, Is.EqualTo(1));
            Assert.That(settings.BaseUrl, Is.EqualTo("http://localhost/v1"));
        });
    }

    [Test]
    public async Task GetLoadSettingsAsync_WhenTheModelListFails_ReturnsTheBaseUrlOnly()
    {
        var client = NewClient(new ScriptedModelHandler
        {
            FailStatus = HttpStatusCode.InternalServerError,
        });

        var settings = await client.GetLoadSettingsAsync(TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(settings.BaseUrl, Is.EqualTo("http://localhost/v1"));
            Assert.That(settings.ContextLength, Is.Null);
            Assert.That(settings.FlashAttention, Is.Null);
            Assert.That(settings.SpeculativeDraftMtp, Is.Null);
            Assert.That(settings.Parallel, Is.Null);
        });
    }

    private static LmStudioAgentClient NewClient(ScriptedModelHandler handler)
    {
        LlmOptions.Configure(["--llm-model", "settings-test-model"]);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "settings-test-model", NullLogger<LmStudioAgentClient>.Instance);
    }

    private static Task<AgentChatMessage> CompleteOnceAsync(LmStudioAgentClient client)
    {
        var messages = new[] { new AgentChatMessage { Role = "user", Content = "hello" } };
        return client.CompleteAsync(messages, [], 256, TestContext.CurrentContext.CancellationToken);
    }
}
