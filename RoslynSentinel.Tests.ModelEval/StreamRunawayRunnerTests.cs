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
/// stop as <see cref="AgentStopReason.StreamRunaway"/> after the client's single retry. A second case checks that a model call
/// outlasting the wall-clock cap is cancelled. No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class StreamRunawayRunnerTests
{
    private const string ModelId = "runaway-test-model";

    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-streamrunaway-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ModelAgentRunner NewRunner(HttpMessageHandler handler, TimeSpan? wallClockCap = null)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
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
