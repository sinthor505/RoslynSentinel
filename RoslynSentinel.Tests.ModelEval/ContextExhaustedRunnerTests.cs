using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// <see cref="ModelAgentRunner"/> with a scripted model that answers every turn with plain text and no tool calls, so the
/// only question is how the runner classifies that tool-less turn: <see cref="AgentStopReason.ContextExhausted"/> when the
/// backend-reported total tokens reached the model's loaded context window, <see cref="AgentStopReason.ModelFinished"/>
/// otherwise. The models endpoint is routed by URL so the context length can be set (or withheld). No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ContextExhaustedRunnerTests
{
    private const string ModelId = "context-test-model";

    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-ctxexhaust-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runDirectory);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(
            clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    [Test]
    public async Task ToolLessTurnAtContextLimit_StopsWithContextExhausted_AndIsNotConverged()
    {
        var runner = NewRunner(contextLength: 4096, totalTokens: 4100);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ContextExhausted));
            Assert.That(result.Converged, Is.False);
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ToolLessTurnBelowContextLimit_StopsWithModelFinished()
    {
        var runner = NewRunner(contextLength: 4096, totalTokens: 1000);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Converged, Is.True);
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ToolLessTurnWithUnknownContextLength_StopsWithModelFinished()
    {
        // The models endpoint answers 404, so the context length resolves to unknown and the token rule cannot fire.
        var runner = NewRunner(contextLength: null, totalTokens: 999999);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Converged, Is.True);
        });
    }

    [Test]
    public async Task ContextSizeExceeded500_StopsWithContextExhausted_NotConverged_OneRequest()
    {
        // LM Studio answers the chat request with HTTP 500 "Context size has been exceeded": the run must stop as
        // ContextExhausted on that single request (no retry), with no turn recorded and the transcript still written.
        var handler = new ContextOverflowHandler(contextLength: 4096);
        var runner = NewRunner(contextLength: 4096, totalTokens: 0, handler: handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ContextExhausted));
            Assert.That(result.Converged, Is.False);
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.ContextExhausted));
            Assert.That(result.Transcript.Turns, Is.Empty);
            Assert.That(handler.ResponsesRequests, Is.EqualTo(1));
            Assert.That(File.Exists(result.TranscriptPath), Is.True);
        });
    }

    [Test]
    public async Task RunWithStamp_TranscriptCarriesCommitAndDirtyFlag()
    {
        var runner = NewRunner(contextLength: 4096, totalTokens: 1000);

        ModelAgentRunner.ProcessStamp = ("abc123", true, "inline", "0123456789ab");
        try
        {
            var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
            Assert.Multiple(() =>
            {
                Assert.That(result.Transcript.CommitHash, Is.EqualTo("abc123"));
                Assert.That(result.Transcript.WorkingTreeDirty, Is.EqualTo(true));
                Assert.That(result.Transcript.ErrorPolicyMode, Is.EqualTo("inline"));
                Assert.That(result.Transcript.ErrorPolicyHash, Is.EqualTo("0123456789ab"));
                Assert.That(json.RootElement.TryGetProperty("CommitHash", out _), Is.True);
                Assert.That(json.RootElement.TryGetProperty("WorkingTreeDirty", out _), Is.True);
                Assert.That(json.RootElement.GetProperty("ErrorPolicyMode").GetString(), Is.EqualTo("inline"));
                Assert.That(json.RootElement.GetProperty("ErrorPolicyHash").GetString(), Is.EqualTo("0123456789ab"));
            });
        }
        finally
        {
            ModelAgentRunner.ProcessStamp = null;
        }
    }

    [Test]
    public async Task RunWithoutStamp_TranscriptOmitsCommitAndDirtyFlag()
    {
        var runner = NewRunner(contextLength: 4096, totalTokens: 1000);

        ModelAgentRunner.ProcessStamp = null;
        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.TryGetProperty("CommitHash", out _), Is.False);
            Assert.That(json.RootElement.TryGetProperty("WorkingTreeDirty", out _), Is.False);
            Assert.That(json.RootElement.TryGetProperty("ErrorPolicyMode", out _), Is.False);
            Assert.That(json.RootElement.TryGetProperty("ErrorPolicyHash", out _), Is.False);
        });
    }

    [Test]
    public void Transcript_WithoutStampProperties_Deserializes()
    {
        var transcript = JsonSerializer.Deserialize<AgentTranscript>("{\"RunId\":\"r1\"}");

        Assert.That(transcript, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(transcript!.RunId, Is.EqualTo("r1"));
            Assert.That(transcript.CommitHash, Is.Null);
            Assert.That(transcript.WorkingTreeDirty, Is.Null);
            Assert.That(transcript.ErrorPolicyMode, Is.Null);
            Assert.That(transcript.ErrorPolicyHash, Is.Null);
        });
    }

    [Test]
    public async Task Run_RecordsToolDescriptionsSchemasAndStopReason()
    {
        var runner = NewRunner(contextLength: 4096, totalTokens: 1000);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
        var transcript = result.Transcript;
        Assert.Multiple(() =>
        {
            Assert.That(transcript.ToolDescriptions, Is.Not.Null);
            Assert.That(transcript.ToolSchemas, Is.Not.Null);
            Assert.That(transcript.ToolDescriptions!.Keys, Is.EquivalentTo(transcript.Tools));
            Assert.That(transcript.ToolSchemas!.Keys, Is.EquivalentTo(transcript.Tools));
            Assert.That(transcript.ToolSchemas!.Values.Select(s => s.ValueKind), Is.All.EqualTo(JsonValueKind.Object));
            Assert.That(transcript.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(json.RootElement.TryGetProperty("ToolDescriptions", out _), Is.True);
            Assert.That(json.RootElement.TryGetProperty("ToolSchemas", out _), Is.True);
            Assert.That(json.RootElement.TryGetProperty("StopReason", out _), Is.True);
        });
    }

    [Test]
    public void Transcript_WithoutToolMetadata_Deserializes()
    {
        const string text = "{\"RunId\":\"r1\",\"SystemPrompt\":\"s\",\"UserPrompt\":\"u\",\"Tools\":[\"t1\"],\"Turns\":[]}";
        var options = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
        };

        var transcript = JsonSerializer.Deserialize<AgentTranscript>(text, options);

        Assert.That(transcript, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(transcript!.RunId, Is.EqualTo("r1"));
            Assert.That(transcript.Tools, Is.EqualTo(new[] { "t1" }));
            Assert.That(transcript.ToolDescriptions, Is.Null);
            Assert.That(transcript.ToolSchemas, Is.Null);
            Assert.That(transcript.StopReason, Is.Null);
        });
    }

    private ModelAgentRunner NewRunner(int? contextLength, int totalTokens, HttpMessageHandler? handler = null)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
        var httpClient = new HttpClient(handler ?? new RoutingHandler(contextLength, totalTokens))
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist stays off (explicit Off) and the consecutive-failure breaker is out of the way: this test is about
        // the tool-less stop classification only.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>
    /// Routes by URL: the models endpoint returns the loaded context length (or 404 when none is given); every other
    /// request is a chat turn answered with one text-only response.completed event carrying the given total_tokens.
    /// </summary>
    private sealed class RoutingHandler(int? contextLength, int totalTokens) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                if (contextLength is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var modelsBody = JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = ModelId, loaded_context_length = contextLength.Value } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(modelsBody, Encoding.UTF8, "application/json"),
                });
            }

            var payload = JsonSerializer.Serialize(new
            {
                response = new
                {
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[] { new { type = "output_text", text = "done" } },
                        },
                    },
                    usage = new { total_tokens = totalTokens },
                },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: response.completed\ndata: {payload}\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    /// <summary>
    /// Answers the models endpoint with the loaded context length, and every /responses POST with HTTP 500 carrying LM Studio's
    /// context-overflow text. Counts the /responses POSTs so a test can assert the client did not retry.
    /// </summary>
    private sealed class ContextOverflowHandler(int contextLength) : HttpMessageHandler
    {
        public int ResponsesRequests
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                var modelsBody = JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = ModelId, loaded_context_length = contextLength } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(modelsBody, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                ResponsesRequests++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"Context size has been exceeded.\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
