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
/// The llama-server branches of <see cref="LmStudioAgentClient"/> and <see cref="ModelAgentRunner"/>. The host
/// 192.168.10.85:8888 is llama-server in the shipped <see cref="EvalHostTable"/>, so the client must read llama-server's
/// <c>models</c> endpoint, never LM Studio's <c>/api/</c> endpoints, and must not apply the LM Studio usage-jump
/// estimate. No llama-server needed: every request is answered by a fake handler.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LlamaServerBackendTests
{
    private const string ModelId = "llama-test-model";
    private const string LlamaServerBaseAddress = "http://192.168.10.85:8888/v1/";
    private const string LlamaServerBaseUrl = "http://192.168.10.85:8888/v1";

    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-llamabackend-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Backend_IsLlamaServer_AndStartupCheckAsksForModelsFirst()
    {
        var handler = new LlamaServerHandler(contextLength: 70000, firstTurnTotal: 0, secondTurnTotal: 0);

        var client = NewClient(handler);

        Assert.Multiple(() =>
        {
            Assert.That(client.Backend, Is.EqualTo(EvalHostTable.BackendLlamaServer));
            Assert.That(handler.Paths, Is.Not.Empty);
            Assert.That(handler.Paths[0], Does.EndWith("/v1/models"));
        });
    }

    [Test]
    public async Task GetContextLengthAsync_ReturnsModelsContextLength_WithoutAnApiRequest()
    {
        var handler = new LlamaServerHandler(contextLength: 70000, firstTurnTotal: 0, secondTurnTotal: 0);
        var client = NewClient(handler);

        var contextLength = await client.GetContextLengthAsync(TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(contextLength, Is.EqualTo(70000));
            Assert.That(handler.Paths.Where(p => p.Contains("/api/", StringComparison.Ordinal)), Is.Empty);
        });
    }

    [Test]
    public async Task GetLoadSettingsAsync_ReturnsContextLengthAndBaseUrlOnly()
    {
        var handler = new LlamaServerHandler(contextLength: 70000, firstTurnTotal: 0, secondTurnTotal: 0);
        var client = NewClient(handler);

        var settings = await client.GetLoadSettingsAsync(TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(settings.BaseUrl, Is.EqualTo(LlamaServerBaseUrl));
            Assert.That(settings.ContextLength, Is.EqualTo(70000));
            Assert.That(settings.FlashAttention, Is.Null);
            Assert.That(settings.SpeculativeDraftMtp, Is.Null);
            Assert.That(settings.Parallel, Is.Null);
        });
    }

    [Test]
    public async Task RunAsync_OnLlamaServer_KeepsImplausibleUsageJumpAsReported_SoContextIsExhausted()
    {
        // Turn 1 reports 25821 (accepted as the first report). Turn 2 reports 72000: a jump of 46179 that the
        // added content cannot explain, so the LM Studio estimate replacement would set the running total to roughly
        // 26000 and finish as ModelFinished. On llama-server the report stands, 72000 >= 70000, and the tool-less
        // turn is ContextExhausted.
        var handler = new LlamaServerHandler(contextLength: 70000, firstTurnTotal: 25821, secondTurnTotal: 72000);
        var runner = NewRunner(handler);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ContextExhausted));
            Assert.That(result.Converged, Is.False);
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(2));
        });
    }

    private static LmStudioAgentClient NewClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(LlamaServerBaseAddress),
        };
        return new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);
    }

    private ModelAgentRunner NewRunner(HttpMessageHandler handler)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);

        // Loop assist stays off so the only turns are the scripted ones.
        return new ModelAgentRunner(
            NewClient(handler), _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>
    /// Answers llama-server's <c>/v1/models</c> with one loaded entry carrying <c>context_length</c>. Scripts the
    /// <c>/responses</c> turns: the first is a tool call (so the runner takes a second turn), later turns are text-only
    /// answers reporting <paramref name="secondTurnTotal"/>. Records every request path.
    /// </summary>
    private sealed class LlamaServerHandler(int contextLength, int firstTurnTotal, int secondTurnTotal) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        private int ResponsesRequests { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                var modelsBody = JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = ModelId, loaded = true, context_length = contextLength } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(modelsBody, Encoding.UTF8, "application/json"),
                });
            }

            if (path.EndsWith("/responses", StringComparison.Ordinal))
            {
                ResponsesRequests++;
                var isFirstTurn = ResponsesRequests == 1;
                object[] output = isFirstTurn
                    ? [new { type = "function_call", call_id = "call-1", name = "GetFileOutline", arguments = "{}" }]
                    : [new { type = "message", content = new[] { new { type = "output_text", text = "done" } } }];
                var total = isFirstTurn ? firstTurnTotal : secondTurnTotal;

                var payload = JsonSerializer.Serialize(new
                {
                    response = new
                    {
                        output,
                        usage = new { total_tokens = total },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"event: response.completed\ndata: {payload}\n\n", Encoding.UTF8, "text/event-stream"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
