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
/// <see cref="ModelAgentRunner"/> against a scripted model that replays each turn's reasoning under a unique marker, so a
/// test can see which turns' reasoning is present in each request body. Covers the pressure-mode drop (projected prompt
/// at 70% of the context), the unknown-context fallback to the block window, and transcript deserialization without the
/// reasoning-replay properties. No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReasoningReplayRunnerTests
{
    private const string ModelId = "reasoning-replay-test-model";
    private const string ToolName = "McpServerStatus";
    private const int ToolTurnCount = 11;

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;
    private ReplayScriptedHandler _handler = null!;
    private string? _savedReplayWindow;

    [SetUp]
    public async Task SetUp()
    {
        _savedReplayWindow = Environment.GetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable);
        Environment.SetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable, null);

        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-reasoning-replay-" + Guid.NewGuid().ToString("N")[..8]);
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
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" });

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
        try
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
        finally
        {
            Environment.SetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable, _savedReplayWindow);
        }
    }

    [Test]
    public async Task PressureMode_DropsOnceTheProjectedPromptReachesSeventyPercent()
    {
        // Turns 1-11 make a tool call; turn 12 is the plain finish. Request index i carries the reasoning of turns 1..i.
        // Totals: the 7000 reported after turn 8 projects the request of turn 9 to >= 70% of 10000, so the oldest two of the
        // five replayable turns (4 and 5) are dropped once. The later low totals do not drop anything further.
        var totals = new[] { 1000, 1000, 1000, 1000, 1000, 1000, 4000, 7000, 1000, 1000, 1000 };
        var runner = NewRunner(contextLength: 10000, totals, ToolTurns(ToolTurnCount));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var bodies = _handler.RequestBodies;
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.ContextLength, Is.EqualTo(10000));
            Assert.That(result.Transcript.ReasoningReplayMode, Is.EqualTo("pressure:0.7,pinned:3"));
            Assert.That(result.Transcript.ReasoningDropTurns, Is.Not.Null);
            Assert.That(result.Transcript.ReasoningDropTurns!, Is.EqualTo(new[] { 9 }));
            Assert.That(bodies, Has.Count.EqualTo(ToolTurnCount + 1));

            // Request index 8 is turn 9: turns 4 and 5 are dropped, pinned turns 1-3 and later turns 6-8 are kept.
            Assert.That(bodies[8], Does.Not.Contain(Marker(4)));
            Assert.That(bodies[8], Does.Not.Contain(Marker(5)));
            Assert.That(bodies[8], Does.Contain(Marker(1)));
            Assert.That(bodies[8], Does.Contain(Marker(2)));
            Assert.That(bodies[8], Does.Contain(Marker(3)));
            Assert.That(bodies[8], Does.Contain(Marker(6)));
            Assert.That(bodies[8], Does.Contain(Marker(7)));
            Assert.That(bodies[8], Does.Contain(Marker(8)));

            // Request index 7 is turn 8, before the drop: every reasoning so far is present.
            for (var k = 1; k <= 7; k++)
            {
                Assert.That(bodies[7], Does.Contain(Marker(k)), $"turn {k} reasoning should be present before the drop");
            }

            Assert.That(CountMarkers(bodies[8]), Is.EqualTo(6));
            Assert.That(CountMarkers(bodies[8]), Is.LessThan(CountMarkers(bodies[7])));

            // The dropped set only grows: the last request still lacks turns 4 and 5.
            Assert.That(bodies[^1], Does.Not.Contain(Marker(4)));
            Assert.That(bodies[^1], Does.Not.Contain(Marker(5)));
        });
    }

    [Test]
    public async Task UnknownContextLength_UsesTheTenTurnBlockWindow_AndOmitsContextLength()
    {
        // The models endpoint answers 404, so the context length is unknown and the policy falls back to the block window.
        var runner = NewRunner(contextLength: null, new[] { 1000 }, ToolTurns(ToolTurnCount));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.ContextLength, Is.Null);
            Assert.That(result.Transcript.ReasoningReplayMode, Is.Not.Null);
            Assert.That(result.Transcript.ReasoningReplayMode, Does.EndWith("(context length unknown)"));
            Assert.That(result.Transcript.ReasoningReplayMode, Is.EqualTo("window:10 (context length unknown)"));
            Assert.That(result.Transcript.ReasoningDropTurns, Is.Null);
        });
    }

    [Test]
    public void Transcript_WithoutReasoningReplayFields_Deserializes()
    {
        var transcript = JsonSerializer.Deserialize<AgentTranscript>("{\"RunId\":\"r1\"}");

        Assert.That(transcript, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(transcript!.RunId, Is.EqualTo("r1"));
            Assert.That(transcript.ContextLength, Is.Null);
            Assert.That(transcript.ReasoningReplayMode, Is.Null);
            Assert.That(transcript.ReasoningDropTurns, Is.Null);
        });
    }

    private static string Marker(int turn) => $"PLAN-MARKER-{turn:D2}-END";

    private static int CountMarkers(string body) => Enumerable.Range(1, ToolTurnCount).Count(k => body.Contains(Marker(k)));

    /// <summary>Turns 1..count each carry their own marker and make a tool call.</summary>
    private static List<ScriptedTurn> ToolTurns(int count) =>
        Enumerable.Range(1, count).Select(k => new ScriptedTurn(Marker(k), null, CallTool: true)).ToList();

    private ModelAgentRunner NewRunner(int? contextLength, int[] totalTokens, IReadOnlyList<ScriptedTurn> script)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
        _handler = new ReplayScriptedHandler(contextLength, totalTokens, script);
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist is off. The breaker is out of the way: the tool calls have distinct arguments, so none is a repeat.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 14,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>One scripted model turn: reasoning text, message content, and whether it makes the <see cref="ToolName"/> call.</summary>
    private sealed record ScriptedTurn(string? Reasoning, string? Content, bool CallTool = false);

    /// <summary>
    /// Answers the models endpoint with the loaded context length (or 404 when none is given). Answers each chat request
    /// with the next scripted turn, then with a plain finish once the script runs out. Each chat response reports the
    /// total_tokens at its request index, clamped to the last element. Records each chat request body.
    /// </summary>
    private sealed class ReplayScriptedHandler(int? contextLength, IReadOnlyList<int> totalTokens, IReadOnlyList<ScriptedTurn> script) : HttpMessageHandler
    {
        private int _chatCalls;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                if (contextLength is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var modelsBody = JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = ModelId, loaded_context_length = contextLength.Value } },
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(modelsBody, Encoding.UTF8, "application/json"),
                };
            }

            if (!path.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var index = _chatCalls++;
            var turn = index < script.Count ? script[index] : new ScriptedTurn(null, "Done.");
            var total = totalTokens[Math.Min(index, totalTokens.Count - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildStream(turn, index, total), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string BuildStream(ScriptedTurn turn, int index, int totalTokens)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(turn.Reasoning))
            {
                var delta = JsonSerializer.Serialize(new { item_id = "rs_" + index, delta = turn.Reasoning });
                sb.Append("event: response.reasoning_text.delta\ndata: ").Append(delta).Append("\n\n");
            }

            var output = new List<object>();
            if (!string.IsNullOrEmpty(turn.Content))
            {
                output.Add(new { type = "message", content = new[] { new { type = "output_text", text = turn.Content } } });
            }

            if (turn.CallTool)
            {
                // reason is mandatory (min 10 chars, has a space) and does not count toward identical-call detection,
                // so the varying toolNameFilter is what keeps each call distinct. A substring filter that matches nothing
                // is still a valid call.
                var arguments = JsonSerializer.Serialize(new { reason = "replay test turn " + index, toolNameFilter = "x" + index });
                output.Add(new { type = "function_call", call_id = "call_" + index, name = ToolName, arguments });
            }

            var completed = JsonSerializer.Serialize(new { response = new { output, usage = new { total_tokens = totalTokens } } });
            sb.Append("event: response.completed\ndata: ").Append(completed).Append("\n\n");
            return sb.ToString();
        }
    }
}
