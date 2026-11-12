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
/// <see cref="ModelAgentRunner"/> with a scripted model that answers each chat turn from a fixed script. Covers the
/// malformed tool-call nudge: a turn whose text holds tool-call markup but no parsed call gets one nudge, and a second
/// consecutive one stops the run. The models endpoint answers 404, so the context-exhaustion rule cannot fire. No LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class MalformedToolCallRunnerTests
{
    private const string ModelId = "malformed-test-model";

    // Markup is built by concatenation: the server's edit guard rejects literal tool-call markup in newContent.
    private const string ToolCallOpen = "<" + "tool_call>";
    private const string ToolCallClose = "<" + "/tool_call>";
    private const string ParameterOpen = "<" + "parameter=reason>";

    private const string NudgePrefix = "Your last reply contained tool-call text";
    private const string ToolName = "McpServerStatus";

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;
    private ScriptedHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-malformed-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task OneMalformedTurnThenFinish_NudgesOnceAndFinishes()
    {
        // Turn 1: markup in the reasoning channel and no call. Turn 2: plain final text.
        var runner = NewRunner(
            new ScriptedTurn("I will call the tool now " + ToolCallOpen + ParameterOpen, null),
            new ScriptedTurn(null, "All done."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var turn1Assists = result.Transcript.Turns[0].Assists;
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(2));
            Assert.That(turn1Assists, Is.Not.Null);
            Assert.That(turn1Assists!, Has.Count.EqualTo(1));
            Assert.That(turn1Assists![0].Kind, Is.EqualTo(AgentAssist.MalformedToolCallKind));
            Assert.That(result.Transcript.AssistCount, Is.EqualTo(1));
            Assert.That(_handler.RequestBodies, Has.Count.EqualTo(2));
            Assert.That(_handler.RequestBodies[1], Does.Contain(NudgePrefix));
        });
    }

    [Test]
    public async Task TwoConsecutiveMalformedTurns_StopsWithMalformedToolCall()
    {
        // Both turns put the markup in the message content and make no call.
        var runner = NewRunner(
            new ScriptedTurn(null, "Reading the file " + ToolCallOpen + "first attempt"),
            new ScriptedTurn(null, ToolCallOpen + ParameterOpen + " second attempt " + ToolCallClose));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.MalformedToolCall));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(2));
            Assert.That(result.TurnCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task MalformedThenRealCallThenMalformed_CounterResets()
    {
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(tools.Select(t => t.Name), Does.Contain(ToolName), "The real-call turn needs ToolName to be exposed.");

        // Turn 1: malformed (nudged). Turn 2: a parsed call resets the counter. Turn 3: malformed again (nudged, not stopped).
        // Turn 4: plain finish.
        var runner = NewRunner(
            new ScriptedTurn(null, "Trying " + ToolCallOpen + "first"),
            new ScriptedTurn(null, null, CallTool: true),
            new ScriptedTurn(null, "Trying again " + ToolCallOpen + ParameterOpen),
            new ScriptedTurn(null, "Finished."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var malformedAssists = result.Transcript.Turns
            .SelectMany(t => t.Assists ?? Enumerable.Empty<AgentAssist>())
            .Where(a => a.Kind == AgentAssist.MalformedToolCallKind)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.Turns, Has.Count.EqualTo(4));
            Assert.That(malformedAssists, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task PlainTextWithoutMarkup_FinishesWithoutAssist()
    {
        var runner = NewRunner(new ScriptedTurn(null, "All done."));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.Turns[0].Assists, Is.Null);
        });
    }

    private ModelAgentRunner NewRunner(params ScriptedTurn[] script)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
        _handler = new ScriptedHandler(script);
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist is off: the nudge path does not depend on it. The breaker is out of the way.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>One scripted model turn: reasoning text, message content, and whether it makes the <see cref="ToolName"/> call.</summary>
    private sealed record ScriptedTurn(string? Reasoning, string? Content, bool CallTool = false);

    /// <summary>
    /// Answers each chat request (POST to responses) with the next scripted turn, then with a plain finish once the script
    /// runs out. Every other request is answered 404 (no context length). Records each chat request body.
    /// </summary>
    private sealed class ScriptedHandler(IReadOnlyList<ScriptedTurn> script) : HttpMessageHandler
    {
        private int _chatCalls;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var index = _chatCalls++;
            var turn = index < script.Count ? script[index] : new ScriptedTurn(null, "Done.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildStream(turn, index), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string BuildStream(ScriptedTurn turn, int index)
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
                output.Add(new { type = "function_call", call_id = "call_" + index, name = ToolName, arguments = "{}" });
            }

            var completed = JsonSerializer.Serialize(new { response = new { output, usage = new { total_tokens = 1000 } } });
            sb.Append("event: response.completed\ndata: ").Append(completed).Append("\n\n");
            return sb.ToString();
        }
    }
}
