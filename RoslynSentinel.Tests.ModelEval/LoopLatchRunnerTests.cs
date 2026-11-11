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
/// The harness side of the server loop latch (proposal_loop_latch.md): <see cref="ModelAgentRunner"/> with a scripted model
/// that sends the same failing call every turn, against the real in-process MCP server with <c>--loop-latch</c> on. The
/// latch refuses the third call; the runner records each refusal as a "latch" assist and stops with
/// <see cref="AgentStopReason.LoopLatchIgnored"/> once the model has called the refused tool twice more. No LM Studio needed.
/// </summary>
// LoopLatchOptions is process-wide static state: do not overlap another fixture that runs tools through a server.
[TestFixture]
[NonParallelizable]
public class LoopLatchRunnerTests
{
    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "Workspace", "SymbolNavigation" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-looplatch-" + Guid.NewGuid().ToString("N")[..8]);
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
        LoopLatchOptions.Configure([]);

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
    public async Task LatchOn_ModelThatIgnoresTheRefusal_StopsWithLoopLatchIgnored_AndTheTurnsCarryLatchAssists()
    {
        LoopLatchOptions.Configure(["--loop-latch"]);
        var runner = NewRunner(turnCap: 12);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var turns = result.Transcript.Turns;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.TranscriptPath));
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.LoopLatchIgnored));

            // Turns 1-2: the real failure, twice. Turn 3: the first refusal. Turns 4-5: the two calls made after it; stop on the second.
            Assert.That(turns, Has.Count.EqualTo(5));
            Assert.That(turns[0].Assists, Is.Null);
            Assert.That(turns[1].Assists, Is.Null);
            Assert.That(turns.Skip(2).Select(t => t.Assists!.Single().Kind), Is.All.EqualTo(AgentAssist.LatchKind));
            Assert.That(turns.Skip(2).Select(t => t.Assists!.Single().RepeatCount), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(turns[2].Assists![0].ToolName, Is.EqualTo("LocateSymbol"));
            Assert.That(turns[2].Assists![0].Text, Does.StartWith("LocateSymbol is DISABLED until another tool succeeds: it failed the same way 2 times."));

            Assert.That(turns[1].ToolCalls.Single().ResultJson, Does.Not.Contain("DISABLED"), "the second failure still executed");
            Assert.That(ToolLoopLatch.IsRefusalBody("LocateSymbol", turns[2].ToolCalls.Single().ResultJson), Is.True);

            Assert.That(doc.RootElement.GetProperty("AssistCount").GetInt32(), Is.EqualTo(3));
            Assert.That(doc.RootElement.GetProperty("Turns")[2].GetProperty("Assists")[0].GetProperty("Kind").GetString(), Is.EqualTo("latch"));
        });
    }

    [Test]
    public async Task LatchOff_SameRun_NeverRefuses_AndRecordsNoLatchAssists()
    {
        LoopLatchOptions.Configure([]);
        Assume.That(LoopLatchOptions.Enabled, Is.False, "ROSLYNSENTINEL_LOOP_LATCH must not be set for this test");
        var runner = NewRunner(turnCap: 5);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(5));
            Assert.That(json, Does.Not.Contain("DISABLED"));
            Assert.That(json, Does.Not.Contain("Assists"));
            Assert.That(result.Transcript.AssistCount, Is.Null);
        });
    }

    private ModelAgentRunner NewRunner(int turnCap)
    {
        LlmOptions.Configure(["--llm-model", "looplatch-test-model"]);
        var httpClient = new HttpClient(new ScriptedHandler(BuildCompletedSse("LocateSymbol", """{"reason":"loop latch test","symbolName":"NoSuchSymbol"}""")))
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, "looplatch-test-model", NullLogger<LmStudioAgentClient>.Instance);

        // repeatedFailureLimit 10 keeps the consecutive-failure breaker out of the way: this test is about the latch stop.
        // Loop assist stays off (explicit Off), so the only assists on a turn are the latch's.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: turnCap,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
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

    private sealed class ScriptedHandler(Func<string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(), Encoding.UTF8, "text/event-stream"),
            });
    }
}
