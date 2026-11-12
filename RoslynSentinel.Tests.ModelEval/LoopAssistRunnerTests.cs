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

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ModelAgentRunner"/> with a scripted model against the real in-process MCP server (same wiring as
/// <see cref="RepeatedToolFailureBreakerTests"/>): the transcript's Tools header, and the loop-assist feature end to
/// end (collapse in the replayed request, injection, transcript Assists/AssistCount, agent.log summary line), plus
/// the same run with loop assist off. No LM Studio needed.
/// </summary>
[TestFixture]
public class LoopAssistRunnerTests
{
    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-loopassist-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task TranscriptHeader_ListsTheToolsOfferedToTheModel_EvenWithLoopAssistOff()
    {
        var offered = (await _mcpClient.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken))
            .Select(t => t.Name).ToList();
        var run = NewRun(LoopAssistOptions.Off, turnCap: 1);

        var result = await run.Runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
        var tools = doc.RootElement.GetProperty("Tools").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(tools, Is.EqualTo(offered), "the header lists exactly the tools sent in the request, in order");
            Assert.That(tools, Does.Contain("LocateSymbol").And.Contain("GetFileOutline"));
            Assert.That(result.Transcript.Tools, Is.EqualTo(offered));
        });
    }

    [Test]
    public async Task LoopAssistOn_CollapsesTheReplayedHistory_InjectsAndFlagsTheTurns()
    {
        var run = NewRun(new LoopAssistOptions(true, "user"), turnCap: 4);

        var result = await run.Runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var turns = result.Transcript.Turns;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
        var lastRequest = run.RequestBodies[^1];
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(turns, Has.Count.EqualTo(4));

            // Turn 1: the first rejection, nothing to assist. Turn 2: the 2nd identical rejection -> injection.
            Assert.That(turns[0].Assists, Is.Null);
            Assert.That(turns[1].Assists!.Select(a => a.Kind), Is.EqualTo(new[] { AgentAssist.InjectionKind }));
            Assert.That(turns[1].Assists![0].Text, Does.StartWith("I have sent the same LocateSymbol call 2 times and it was rejected the same way each time."));
            Assert.That(turns[1].Assists![0].Text, Does.EndWith("My next call must be a different tool."));

            // Turn 3's request is the first that carries a collapsed history; turn 3 also gets its own injection.
            Assert.That(turns[2].Assists!.Select(a => a.Kind), Is.EqualTo(new[] { AgentAssist.CollapseKind, AgentAssist.InjectionKind }));
            Assert.That(turns[3].Assists!.Select(a => (a.Kind, a.RepeatCount)),
                Is.EqualTo(new[] { (AgentAssist.CollapseKind, 2), (AgentAssist.InjectionKind, 4) }));

            Assert.That(doc.RootElement.GetProperty("AssistCount").GetInt32(), Is.EqualTo(5));
            Assert.That(doc.RootElement.GetProperty("Turns")[1].GetProperty("Assists")[0].GetProperty("Kind").GetString(), Is.EqualTo("injection"));
            Assert.That(doc.RootElement.GetProperty("Turns")[0].TryGetProperty("Assists", out _), Is.False);

            // The raw transcript keeps all four calls; the 4th request replayed only the first of the 3 earlier ones.
            Assert.That(turns.Sum(t => t.ToolCalls.Count), Is.EqualTo(4));
            Assert.That(CountOf(lastRequest, "\"arguments\":"), Is.EqualTo(1));
            Assert.That(lastRequest, Does.Contain("(sent 2 more times, rejected identically)"));
            Assert.That(lastRequest, Does.Contain("I have sent the same LocateSymbol call 2 times"));

            Assert.That(run.Logger.Lines.Single(l => l.StartsWith("Agent run finished:")),
                Does.EndWith("Run failed with 5 assists."));
        });
    }

    [Test]
    public async Task LoopAssistOff_RequestsAndTranscriptCarryNoAssistFields()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 4);

        var result = await run.Runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        var lastRequest = run.RequestBodies[^1];
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("Assists"));
            Assert.That(json, Does.Not.Contain("AssistCount"));
            Assert.That(result.Transcript.Turns.All(t => t.Assists is null), Is.True);
            Assert.That(CountOf(lastRequest, "\"arguments\":"), Is.EqualTo(3), "every earlier call is replayed unchanged");
            Assert.That(lastRequest, Does.Not.Contain("rejected identically"));
            Assert.That(lastRequest, Does.Not.Contain("I have sent the same"));
            Assert.That(run.Logger.Lines.Single(l => l.StartsWith("Agent run finished:")), Does.Not.Contain("Run "));
        });
    }

    [Test]
    public async Task SeededTurns_LoopAssistOn_TrackerCountsTheSeededRepeat_AndTheFirstLiveRequestCarriesTheHistory()
    {
        var run = NewRun(new LoopAssistOptions(true, "user"), turnCap: 4);

        var result = await run.Runner.RunAsync(
            "seed-system", "seed-user", _runDirectory, TestContext.CurrentContext.CancellationToken, seedTurns: ThreeSeedTurns());

        var turns = result.Transcript.Turns;
        var body = run.RequestBodies[^1]; // the one live completion request (an earlier entry may be the context-length probe)
        Assert.Multiple(() =>
        {
            // The seeded turns come first, numbered as recorded; the one live turn continues from them.
            Assert.That(turns.Select(t => t.TurnNumber), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(result.TurnCount, Is.EqualTo(4));
            Assert.That(turns[0].Assists, Is.Null);
            Assert.That(turns[1].Assists, Is.Null, "the first rejection is not a repeat");

            // 2 identical rejections inside the seed: the injection fires on turn 3, before any live turn.
            Assert.That(turns[2].Assists!.Select(a => (a.Kind, a.RepeatCount)), Is.EqualTo(new[] { (AgentAssist.InjectionKind, 2) }));
            Assert.That(result.Transcript.AssistCount, Is.GreaterThanOrEqualTo(1));

            // The first live request replays the seeded history in order, with the repeat collapsed and the injection after it.
            var order = new[] { "seed-user", "seed turn 1", "seed-outline-result", "seed turn 2", "seed turn 3", "I have sent the same LocateSymbol call 2 times" }
                .Select(marker => body.IndexOf(marker, StringComparison.Ordinal)).ToList();
            Assert.That(order, Has.All.GreaterThanOrEqualTo(0), "every seeded marker reaches the request");
            Assert.That(order, Is.Ordered);
            Assert.That(body, Does.Contain("seed-system"));
            Assert.That(body, Does.Contain("(sent 1 more time, rejected identically)"));
            Assert.That(CountOf(body, "\"arguments\":"), Is.EqualTo(2), "call 1 and the first rejection; the repeat is collapsed");
        });
    }

    [Test]
    public async Task SeededTurns_LoopAssistOff_ReplayTheSeededHistoryUnchanged()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 4);

        var result = await run.Runner.RunAsync(
            "seed-system", "seed-user", _runDirectory, TestContext.CurrentContext.CancellationToken, seedTurns: ThreeSeedTurns());

        var body = run.RequestBodies[^1];
        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        Assert.Multiple(() =>
        {
            Assert.That(result.Transcript.Turns.Select(t => t.TurnNumber), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(result.Transcript.Turns.Take(3).SelectMany(t => t.ToolCalls).Select(c => c.ToolName),
                Is.EqualTo(new[] { "GetFileOutline", "LocateSymbol", "LocateSymbol" }));
            Assert.That(json, Does.Not.Contain("Assists"));
            Assert.That(CountOf(body, "\"arguments\":"), Is.EqualTo(3), "every seeded call is replayed");
            Assert.That(body, Does.Not.Contain("I have sent the same"));
            Assert.That(body, Does.Not.Contain("rejected identically"));
            Assert.That(
                new[] { "seed-user", "seed turn 1", "seed-outline-result", "seed turn 2", "seed turn 3" }
                    .Select(marker => body.IndexOf(marker, StringComparison.Ordinal)),
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task TargetReached_TruePredicateAfterLiveTurn2_StopsWithTargetReached()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 6);
        var calls = 0;

        var result = await run.Runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken,
            targetReached: _ => Task.FromResult(++calls == 2));

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TargetReached));
            Assert.That(result.TurnCount, Is.EqualTo(2));
            Assert.That(result.Transcript.StopReason, Is.EqualTo(AgentStopReason.TargetReached));
            Assert.That(result.Converged, Is.False);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(run.Logger.Lines, Has.Some.Contains("Agent run finished: TargetReached"));
        });
    }

    [Test]
    public async Task TargetReached_NeverTrue_RunsToTheCap()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 3);
        var calls = 0;

        var result = await run.Runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken,
            targetReached: _ =>
            {
                calls++;
                return Task.FromResult(false);
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(calls, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task TargetReached_IsNotEvaluatedOnSeededTurns()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 5);
        var calls = 0;

        var result = await run.Runner.RunAsync(
            "seed-system", "seed-user", _runDirectory, TestContext.CurrentContext.CancellationToken,
            seedTurns: ThreeSeedTurns(),
            targetReached: _ => Task.FromResult(++calls == 1));

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TargetReached));
            Assert.That(result.TurnCount, Is.EqualTo(4), "3 seeded turns plus 1 live turn");
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TargetReached_ThrowingPredicate_DoesNotFailTheRun()
    {
        var run = NewRun(LoopAssistOptions.Off, turnCap: 3);

        var result = await run.Runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken,
            targetReached: _ => throw new InvalidOperationException("predicate broke"));

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(run.Logger.Lines, Has.Some.Contains("target predicate threw"));
        });
    }

    private const string SeedLocateArguments = """{"reason":"loop assist test","symbolName":"NoSuchSymbol"}""";

    private const string SeedRejection =
        """{"isError":true,"errorData":{"errorCode":"NotFound","message":"LocateSymbol: nothing matched. Next step: use Search instead."}}""";

    /// <summary>The first three turns of a resumed run: a successful read, then the same call rejected twice.</summary>
    private static List<AgentTranscriptTurn> ThreeSeedTurns() =>
    [
        SeedTurn(1, "call_seed_1", "GetFileOutline", """{"filePath":"a.cs"}""", false, """{"isError":false,"outline":"seed-outline-result"}"""),
        SeedTurn(2, "call_seed_2", "LocateSymbol", SeedLocateArguments, true, SeedRejection),
        SeedTurn(3, "call_seed_3", "LocateSymbol", SeedLocateArguments, true, SeedRejection),
    ];

    private static AgentTranscriptTurn SeedTurn(
        int turnNumber, string callId, string tool, string argumentsJson, bool isError, string resultJson) => new()
    {
        TurnNumber = turnNumber,
        ModelLatency = TimeSpan.FromSeconds(1),
        StartedAt = DateTimeOffset.Now,
        CompletedAt = DateTimeOffset.Now,
        ModelMessage = new AgentChatMessage
        {
            Role = "assistant",
            Content = $"seed turn {turnNumber}",
            ToolCalls = [new AgentToolCall { Id = callId, Name = tool, ArgumentsJson = argumentsJson }],
        },
        ToolCalls =
        [
            new AgentToolCallRecord
            {
                ToolName = tool,
                ArgumentsJson = argumentsJson,
                ResultJson = resultJson,
                IsError = isError,
                Latency = TimeSpan.FromMilliseconds(5),
                StartedAt = DateTimeOffset.Now,
                CompletedAt = DateTimeOffset.Now,
                ToolCallId = callId,
            },
        ],
    };

    private (ModelAgentRunner Runner, List<string> RequestBodies, ListLogger Logger) NewRun(LoopAssistOptions options, int turnCap)
    {
        var bodies = new List<string>();
        var logger = new ListLogger();
        RoslynSentinel.Common.LlmOptions.Configure(["--llm-model", "loopassist-test-model"]);
        var httpClient = new HttpClient(new ScriptedHandler(bodies, BuildCompletedSse("LocateSymbol", """{"reason":"loop assist test","symbolName":"NoSuchSymbol"}""")))
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new RoslynSentinel.Common.LmStudioAgentClient(
            httpClient, "loopassist-test-model", NullLogger<RoslynSentinel.Common.LmStudioAgentClient>.Instance);

        // repeatedFailureLimit 10 keeps the breaker out of the way: this test is about the assist, not the stop.
        var runner = new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: turnCap,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: logger,
            loopAssist: options);
        return (runner, bodies, logger);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>One response.completed event whose output is a single function_call (a fresh call id each time).</summary>
    private static Func<string> BuildCompletedSse(string toolName, string argumentsJson) => () =>
    {
        var payload = JsonSerializer.Serialize(new
        {
            response = new
            {
                output = new[]
                {
                    new
                    {
                        type = "function_call",
                        call_id = "call_" + Guid.NewGuid().ToString("N")[..8],
                        name = toolName,
                        arguments = argumentsJson,
                    },
                },
            },
        });

        return $"event: response.completed\ndata: {payload}\n\n";
    };

    private sealed class ScriptedHandler(List<string> requestBodies, Func<string> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(), Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Captures the rendered text of every log call, so a test can read the run-summary line.</summary>
    private sealed class ListLogger : ILogger<ModelAgentRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
