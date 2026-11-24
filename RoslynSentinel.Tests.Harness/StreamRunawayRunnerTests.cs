using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ModelAgentRunner"/> with a scripted model whose streamed responses never finish cleanly. Every chat request
/// streams a tool call with placeholder arguments, which <see cref="LmStudioAgentClient"/> treats as a runaway: the runner must
/// stop as <see cref="AgentStopReason.StreamRunaway"/> after the client's single retry. A stream that ends without
/// <c>response.completed</c> must stop as <see cref="AgentStopReason.StreamTruncated"/> the same way. A host that echoes a sampler value
/// other than the one sent must stop as <see cref="AgentStopReason.SamplerMismatch"/> without a retry. A further case checks that a model call
/// outlasting the wall-clock cap is cancelled. No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class StreamRunawayRunnerTests
{
    private const string ModelId = "runaway-test-model";

    private ModelEvalFakeModelEnvironment? _environment;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _environment = await ModelEvalFakeModelEnvironment.CreateAsync(
            TestContext.CurrentContext.CancellationToken, runDirectoryPrefix: "rs-streamrunaway-");
        _mcpClient = _environment.Client;
        _runDirectory = _environment.RunDirectory!;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
    }

    [Test]
    public async Task PlaceholderToolCallsOnBothAttempts_StopsWithStreamRunaway_NotConverged()
    {
        // Each attempt streams one tool call with placeholder arguments, so the client aborts the stream and retries once;
        // the second attempt does the same and the exception reaches the runner, which must stop without recording a turn.
        var handler = new RunawayHandler();
        var runner = NewRunner(handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.StreamRunaway));
            Assert.That(result.Converged, Is.False);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(2));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    [Test]
    public async Task StreamEndingWithoutResponseCompleted_OnBothAttempts_StopsWithStreamTruncated_NoException()
    {
        // Each attempt streams a text delta and closes without response.completed, so the client throws StreamTruncatedException,
        // retries once (it is an InvalidOperationException, caught by the generic retry filter) and throws again; the runner must
        // end the run with a transcript instead of letting the exception escape to RunAsync.
        var handler = new TruncatedStreamHandler();
        var runner = NewRunner(handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.StreamTruncated));
            Assert.That(result.Converged, Is.False);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(2));
            Assert.That(result.Transcript, Is.Not.Null);
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.StreamTruncated));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    [Test]
    public async Task StreamEndingWithoutResponseCompleted_AfterStreamingTheTokenCap_IsNotRetried()
    {
        // The attempt streams ~33k chars (~8.2k estimated tokens, over 90% of the 8192-token cap) and closes without
        // response.completed: the retry would stream the same amount again, so the client must throw once and not retry.
        var longText = string.Concat(Enumerable.Range(1, 1100).Select(n => $"Distinct sentence number {n} carries its own words. "));
        var handler = new ScriptedStreamHandler(SseDelta("response.output_text.delta", longText));
        var runner = NewRunner(handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.StreamTruncated));
            Assert.That(result.Converged, Is.False);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(1));
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.StreamTruncated));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    [Test]
    public async Task ContentChannelPhraseLoop_AbortsAfterTheTwentiethRepeat_NotRetried()
    {
        // Thirty one-sentence deltas; the detector trips on the 20th occurrence, so the client aborts with one request.
        var body = string.Concat(Enumerable.Repeat(SseDelta("response.output_text.delta", "The tests will fail. "), 30));
        var handler = new ScriptedStreamHandler(body);
        var runner = NewRunner(handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ReasoningLoopDetected));
            Assert.That(result.Converged, Is.False);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(1));
            Assert.That(result.Transcript, Is.Not.Null);
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.ReasoningLoopDetected));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    [Test]
    public async Task ReasoningChannelPhraseLoop_AbortsAfterTheTwentiethRepeat_NotRetried()
    {
        var body = string.Concat(Enumerable.Repeat(SseDelta("response.reasoning_text.delta", "The tests will fail. "), 30));
        var handler = new ScriptedStreamHandler(body);
        var runner = NewRunner(handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ReasoningLoopDetected));
            Assert.That(handler.ResponsesRequests, Is.EqualTo(1));
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.ReasoningLoopDetected));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    private static string SseDelta(string eventType, string text)
    {
        var payload = JsonSerializer.Serialize(new { item_id = "item_1", delta = text });
        return $"event: {eventType}\ndata: {payload}\n\n";
    }

    /// <summary>
    /// Every /responses POST answers the same fixed SSE body (no response.completed unless the body includes one). The models
    /// endpoint answers 404. Counts the /responses POSTs so a test can assert how many attempts the client made.
    /// </summary>
    private sealed class ScriptedStreamHandler(string sseBody) : HttpMessageHandler
    {
        public int ResponsesRequests
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ResponsesRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    [Test]
    public async Task SamplerValueEchoedDifferently_StopsWithSamplerMismatch_NoException()
    {
        // The run sends temperature 0.7; the host echoes 0.3 in response.completed. LmStudioAgentClient throws SamplerMismatchException,
        // which is not an InvalidOperationException, so there is no retry; the runner must end the run with a transcript instead of
        // letting the exception escape to RunAsync.
        var handler = new SamplerEchoHandler(echoedTemperature: 0.3);
        var runner = NewRunner(handler: handler, temperature: "0.7");

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.SamplerMismatch));
            Assert.That(result.Converged, Is.False);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(1));
            Assert.That(result.Transcript, Is.Not.Null);
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.SamplerMismatch));
            Assert.That(result.Transcript.Turns, Is.Empty);
        });
    }

    [Test]
    public async Task ModelCallOutlastingTheWallClockCap_IsCancelled_StopsWithWallClockCapExceeded()
    {
        // The chat request never answers; the runner's per-call deadline (the 1 s cap) must cancel it instead of waiting out the 30 s delay.
        var runner = NewRunner(handler: new StalledResponseHandler(), wallClockCap: TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);
        stopwatch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.WallClockCapExceeded));
            Assert.That(result.Converged, Is.False);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
        });
    }

    private ModelAgentRunner NewRunner(HttpMessageHandler handler, TimeSpan? wallClockCap = null, string? temperature = null)
    {
        // LlmOptions is static: each call reconfigures it, so a test that needs a sent temperature passes it explicitly.
        LlmOptions.Configure(temperature is null
            ? ["--llm-model", ModelId]
            : ["--llm-model", ModelId, "--llm-temperature", temperature]);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist stays off (explicit Off) and the consecutive-failure breaker is out of the way: these tests are about
        // the stream-level and wall-clock stops only.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: wallClockCap ?? TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>
    /// Every /responses POST streams one function_call with placeholder arguments. The models endpoint answers 404, so the
    /// context length stays unknown. Counts the /responses POSTs so a test can assert the client retried exactly once.
    /// </summary>
    private sealed class RunawayHandler : HttpMessageHandler
    {
        public int ResponsesRequests
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ResponsesRequests++;
            var payload = JsonSerializer.Serialize(new
            {
                item = new
                {
                    type = "function_call",
                    call_id = "call_" + ResponsesRequests,
                    name = "ReadFile",
                    arguments = "{\"reason\":\"...\",\"filePath\":\"...\"}",
                },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: response.output_item.done\ndata: {payload}\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    /// <summary>
    /// Every /responses POST streams one text delta and then ends the body without a response.completed event. The models
    /// endpoint answers 404. Counts the /responses POSTs so a test can assert the client retried exactly once.
    /// </summary>
    private sealed class TruncatedStreamHandler : HttpMessageHandler
    {
        public int ResponsesRequests
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ResponsesRequests++;
            var payload = JsonSerializer.Serialize(new { item_id = "msg_1", delta = "partial" });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: response.output_text.delta\ndata: {payload}\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    /// <summary>
    /// Every /responses POST streams one response.completed event whose response echoes a temperature other than the one the
    /// run sent. The models endpoint answers 404. Counts the /responses POSTs so a test can assert there was no retry.
    /// </summary>
    private sealed class SamplerEchoHandler(double echoedTemperature) : HttpMessageHandler
    {
        public int ResponsesRequests
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ResponsesRequests++;
            var payload = JsonSerializer.Serialize(new
            {
                response = new
                {
                    output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "done" } } } },
                    temperature = echoedTemperature,
                },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: response.completed\ndata: {payload}\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    /// <summary>
    /// Answers the models endpoint with 404 and holds every /responses POST for 30 seconds unless the request is cancelled.
    /// </summary>
    private sealed class StalledResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
