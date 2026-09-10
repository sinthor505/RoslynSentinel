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

using RoslynSentinel.Tests.ModelEval.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Covers <see cref="ModelAgentRunner"/>'s repeated-failure breaker with a scripted model instead
/// of a real one, so it runs in the normal test pass rather than needing LM Studio.
/// <para>
/// This guards the gap that let run 20260910-013550-398 burn 23 of its 60 turns re-issuing one
/// failing call: the only per-tool error budget at the time was
/// <see cref="AgentLoop.AgentToolErrorAssertions"/>, an assertion evaluated *after* a run finished,
/// which by construction cannot shorten one. The breaker is the in-loop counterpart, so the thing
/// worth testing is that it actually ends the loop early — not merely that it counts.
/// </para>
/// </summary>
[TestFixture]
public class RepeatedToolFailureBreakerTests
{
    // "Workspace" is where LocateSymbol/GetFileOutline live (SentinelWorkspaceTools /
    // SentinelSymbolTools) — see ToolClassRegistry.AdvancedModeToToolClasses. Not the project
    // names "Basic"/"Advanced", which aren't mode names at all and silently resolve to nothing.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-breaker-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runDirectory);

        // Same in-memory client/server pipe wiring as TranscriptReplayTests — a real MCP server so
        // the failures the breaker counts are real tool failures, not hand-written stubs.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
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
    public async Task Breaker_TripsAndStopsTheLoop_WhenTheSameCallKeepsFailing()
    {
        // No solution is loaded, so every LocateSymbol call fails identically — the same shape as
        // the real livelock, where each retry differed only in content the signature ignores.
        var runner = NewRunner(repeatedFailureLimit: 3, turnCap: 30, alwaysCall: LocateSymbolCall);

        var result = await runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.RepeatedToolFailure));
            Assert.That(result.Converged, Is.False, "A tripped breaker must not read as a converged run.");
            Assert.That(result.TurnCount, Is.EqualTo(3),
                "The loop should end on the 3rd identical failure, not run to the turn cap of 30.");
            Assert.That(result.RepeatedFailure, Is.Not.Null);
            Assert.That(result.RepeatedFailure!.ToolName, Is.EqualTo("LocateSymbol"));
            Assert.That(result.RepeatedFailure.FailureCount, Is.EqualTo(3));
            Assert.That(result.RepeatedFailure.FirstTurn, Is.EqualTo(1));
            Assert.That(result.RepeatedFailure.LastTurn, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Breaker_DoesNotTrip_WhenFailuresAreNotConsecutive()
    {
        // Alternating failing tools: three failures overall by turn 3, but never the same one
        // twice running. A model probing different tools is exploring, not stuck, so the run
        // should reach its (small) turn cap instead of being cut short.
        var runner = NewRunner(
            repeatedFailureLimit: 2, turnCap: 4,
            alternating: [LocateSymbolCall, GetFileOutlineCall]);

        var result = await runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(result.RepeatedFailure, Is.Null);
            Assert.That(result.TurnCount, Is.EqualTo(4));
        });
    }

    [Test]
    public void Constructor_RejectsALimitBelowOne()
    {
        // Guards against "disable it by passing 0" — the parameter is required precisely so every
        // call site states a real tolerance; a silent no-op limit would recreate the original gap.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelAgentRunner(NewAgentClient(_ => ""), _mcpClient, repeatedFailureLimit: 0));
    }

    private ModelAgentRunner NewRunner(
        int repeatedFailureLimit,
        int turnCap,
        (string Tool, string Args)? alwaysCall = null,
        (string Tool, string Args)[]? alternating = null)
    {
        var turn = 0;
        var agentClient = NewAgentClient(_ =>
        {
            var call = alwaysCall ?? alternating![turn++ % alternating.Length];
            return BuildCompletedSse(call.Tool, call.Args);
        });

        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: repeatedFailureLimit,
            turnCap: turnCap,
            wallClockCap: TimeSpan.FromMinutes(2));
    }

    private static (string Tool, string Args) LocateSymbolCall =>
        ("LocateSymbol", """{"reason":"breaker test","symbolName":"NoSuchSymbol"}""");

    private static (string Tool, string Args) GetFileOutlineCall =>
        ("GetFileOutline", """{"reason":"breaker test","filePath":"C:\\nope\\Missing.cs"}""");

    private static LmStudioAgentClient NewAgentClient(Func<HttpRequestMessage, string> respond)
    {
        // LlmOptions is process-global and Model has no public setter, so it's configured the same
        // way the real entry points do it: via Configure(args). Harmless to call repeatedly across
        // tests in this fixture — every field it derives from --llm-model is deterministic.
        RoslynSentinel.Common.LlmOptions.Configure(["--llm-model", "breaker-test-model"]);
        var httpClient = new HttpClient(new ScriptedHandler(respond))
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, NullLogger<LmStudioAgentClient>.Instance);
    }

    /// <summary>
    /// The smallest SSE stream <see cref="LmStudioAgentClient"/> accepts: one response.completed
    /// event whose output is a single function_call. Everything else it handles (reasoning deltas,
    /// output_text deltas, output_item.done) is optional logging detail.
    /// </summary>
    private static string BuildCompletedSse(string toolName, string argumentsJson)
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
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "text/event-stream"),
            });
    }
}
