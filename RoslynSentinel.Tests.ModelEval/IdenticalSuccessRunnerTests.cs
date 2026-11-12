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
/// <see cref="ModelAgentRunner"/> with a scripted model that answers each chat turn from a fixed script. Covers the
/// identical-success nudge: the same successful call three times in a row gets one nudge, and the next identical call
/// after that nudge ends the run with <see cref="AgentStopReason.RepeatedIdenticalCall"/>. The fake tool is the real
/// McpServerStatus, which succeeds with no solution loaded. No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdenticalSuccessRunnerTests
{
    private const string ModelId = "identical-success-test-model";

    // McpServerStatus succeeds without a loaded solution and is exposed under the same modes as the malformed-call tests.
    private const string ToolName = "McpServerStatus";
    private const string SuccessArgs = "{\"reason\":\"identical success test run\"}";

    // Same tool, different non-reason argument: a different call, so it resets the streak.
    private const string OtherArgs = "{\"reason\":\"identical success test run\",\"toolListing\":\"inactive\"}";

    // The literal counts below mirror ModelAgentRunner.IdenticalSuccessNudgeAt (3): the nudge lands on the 3rd identical
    // call and the stop on the 4th. If that constant changes, these tests need the same change.

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;
    private ScriptedHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-identical-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Run_NudgesOnce_OnThirdIdenticalSuccessfulCall()
    {
        // Turns 1-3 make the same successful call; the nudge lands on turn 3. Turn 4 is a plain finish.
        var runner = NewRunner(
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Finish("All done."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var allAssists = result.Transcript.Turns
            .SelectMany(t => t.Assists ?? Enumerable.Empty<AgentAssist>())
            .ToList();
        var turn3Assists = result.Transcript.Turns[2].Assists;
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(4));
            Assert.That(allAssists, Has.Count.EqualTo(1));
            Assert.That(turn3Assists, Is.Not.Null);
            Assert.That(turn3Assists!, Has.Count.EqualTo(1));
            Assert.That(turn3Assists![0].Kind, Is.EqualTo(AgentAssist.IdenticalCallKind));
            Assert.That(turn3Assists[0].ToolName, Is.EqualTo(ToolName));
            Assert.That(turn3Assists[0].RepeatCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Run_Stops_WhenIdenticalCallRepeatsAfterNudge()
    {
        // Four identical calls: the 3rd is nudged, the 4th ends the run. The final plain turn must never be requested.
        var runner = NewRunner(
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Finish("Should not be reached."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.RepeatedIdenticalCall));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(4));
            Assert.That(result.TurnCount, Is.EqualTo(4));
            Assert.That(_handler.ChatRequestCount, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task Run_ResetsStreak_WhenADifferentCallRunsBetween()
    {
        // A, A, B, A, A: the different call B breaks the streak, so no run of three identical calls ever happens.
        var runner = NewRunner(
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Call(ToolName, OtherArgs),
            Call(ToolName, SuccessArgs),
            Call(ToolName, SuccessArgs),
            Finish("All done."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var identicalAssists = result.Transcript.Turns
            .SelectMany(t => t.Assists ?? Enumerable.Empty<AgentAssist>())
            .Where(a => a.Kind == AgentAssist.IdenticalCallKind)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(identicalAssists, Is.Empty);
        });
    }

    [Test]
    public async Task Run_TreatsReasonOnlyDifferencesAsIdentical()
    {
        // Same tool and arguments apart from "reason", which the canonical form drops: four calls still count as identical.
        var runner = NewRunner(
            Call(ToolName, ArgsWithReason("identical reason variant one")),
            Call(ToolName, ArgsWithReason("identical reason variant two")),
            Call(ToolName, ArgsWithReason("identical reason variant three")),
            Call(ToolName, ArgsWithReason("identical reason variant four")),
            Finish("Should not be reached."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.RepeatedIdenticalCall));
    }

    private static string ArgsWithReason(string reason) => "{\"reason\":\"" + reason + "\"}";

    private ModelAgentRunner NewRunner(params ScriptedTurn[] script)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
        _handler = new ScriptedHandler(script);
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist is off so the only assists recorded are the ones under test. The repeated-failure breaker is out of the way.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 10,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    private static ScriptedTurn Call(string tool, string argumentsJson) => new(tool, argumentsJson, null);

    private static ScriptedTurn Finish(string text) => new(null, null, text);

    /// <summary>One scripted model turn: either a tool call (Tool and ArgumentsJson) or a plain message (Text).</summary>
    private sealed record ScriptedTurn(string? Tool, string? ArgumentsJson, string? Text);

    /// <summary>
    /// Answers each chat request (POST to responses) with the next scripted turn, then with a plain finish once the script
    /// runs out. Every other request is answered 404 (no context length). Counts the chat requests it answers.
    /// </summary>
    private sealed class ScriptedHandler(IReadOnlyList<ScriptedTurn> script) : HttpMessageHandler
    {
        private int _chatRequests;

        public int ChatRequestCount => _chatRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            await request.Content!.ReadAsStringAsync(cancellationToken);
            var index = _chatRequests++;
            var turn = index < script.Count ? script[index] : new ScriptedTurn(null, null, "Done.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildCompletedSse(turn, index), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string BuildCompletedSse(ScriptedTurn turn, int index)
        {
            var output = new List<object>();
            if (turn.Tool is not null)
            {
                output.Add(new { type = "function_call", call_id = "call_" + index, name = turn.Tool, arguments = turn.ArgumentsJson });
            }

            if (turn.Text is not null)
            {
                output.Add(new { type = "message", content = new[] { new { type = "output_text", text = turn.Text } } });
            }

            var payload = JsonSerializer.Serialize(new { response = new { output } });
            return "event: response.completed\ndata: " + payload + "\n\n";
        }
    }
}
