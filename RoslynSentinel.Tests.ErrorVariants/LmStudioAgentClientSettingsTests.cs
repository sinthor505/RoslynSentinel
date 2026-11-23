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

    [Test]
    public async Task LoadedModelStatusCheck_TargetsTheHttpClientsHost_NotTheGlobalLlmOptionsBaseUrl()
    {
        LlmOptions.Configure(["--llm-model", "settings-test-model"]);
        var firstGet = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpClient = new HttpClient(new RecordingModelsHandler(firstGet))
        {
            BaseAddress = new Uri("http://other-host.invalid:1234/v1/"),
        };
        _ = new LmStudioAgentClient(httpClient, "settings-test-model", NullLogger<LmStudioAgentClient>.Instance);

        var completed = await Task.WhenAny(firstGet.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.That(completed, Is.SameAs(firstGet.Task), "The loaded-model check never issued a GET.");
        Assert.That(firstGet.Task.Result.ToString(), Is.EqualTo("http://other-host.invalid:1234/api/v1/models"));
    }

    private sealed class RecordingModelsHandler(TaskCompletionSource<Uri> firstGet) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                firstGet.TrySetResult(request.RequestUri!);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[]}"),
            });
        }
    }

    [Test]
    public async Task CompleteAsync_WhenTheEchoedTemperatureDiffersFromTheSentOne_ThrowsSamplerMismatch()
    {
        var handler = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("done", 0.3));
        var client = NewClient(handler, "--llm-temperature", "0.7");

        var ex = await Assert.ThrowsAsync<SamplerMismatchException>(() => CompleteOnceAsync(client));

        Assert.That(ex.Message, Does.Contain("temperature").And.Contain("0.7").And.Contain("0.3"));
        // SamplerMismatchException is not an InvalidOperationException, so CompleteAsync does not retry it.
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CompleteAsync_WhenTheEchoedTopPDiffersFromTheSentOne_ThrowsSamplerMismatch()
    {
        var client = NewClient(new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("done", null, 0.5)),
            "--llm-top-p", "0.9");

        var ex = await Assert.ThrowsAsync<SamplerMismatchException>(() => CompleteOnceAsync(client));

        Assert.That(ex.Message, Does.Contain("top_p").And.Contain("0.9").And.Contain("0.5"));
    }

    [Test]
    public async Task CompleteAsync_WhenEchoedValuesMatchWhatWasSent_Completes()
    {
        var client = NewClient(new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("done", 0.7, 0.9)),
            "--llm-temperature", "0.7", "--llm-top-p", "0.9");

        var reply = await CompleteOnceAsync(client);

        Assert.That(reply, Is.Not.Null);
        Assert.That(client.ReportedSampler, Is.Not.Null);
    }

    private static LmStudioAgentClient NewClient(ScriptedModelHandler handler, params string[] extraArgs)
    {
        LlmOptions.Configure(["--llm-model", "settings-test-model", .. extraArgs]);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "settings-test-model", NullLogger<LmStudioAgentClient>.Instance);
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
