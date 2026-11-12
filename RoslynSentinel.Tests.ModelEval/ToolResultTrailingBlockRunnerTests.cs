using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// A tool result with several text content blocks reaches the eval model in full: <see cref="ModelAgentRunner"/> keeps
/// <c>ResultJson</c> as the first block (JSON consumers parse it) and carries the trailing blocks in
/// <see cref="AgentToolCallRecord.TrailingText"/>, appended to the tool message the model reads. Scripted model, in-process
/// MCP server with test-only tools; no LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolResultTrailingBlockRunnerTests
{
    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-trailingblock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runDirectory);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTools<TrailingBlockTestTools>();

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
    public async Task TwoBlocks_ModelSeesTheTrailingBlock_ResultJsonStaysFirstBlock_AndTranscriptCarriesIt()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("TwoBlocks", "{}"),
            FinalMessageSse("done"));
        var runner = NewRunner(model);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(model.Requests[1], Does.Contain("{\\\"ok\\\":true}"), "the first block is the tool output");
            Assert.That(model.Requests[1], Does.Contain("TRAILING NOTE"), "the trailing block reaches the model");
            Assert.That(record.ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(record.TrailingText, Is.EqualTo("TRAILING NOTE"));
            Assert.That(record.IsError, Is.False);
            Assert.That(json, Does.Contain("TrailingText"));
        });
    }

    [Test]
    public async Task OneBlock_ToolOutputIsResultJsonExactly_TrailingTextIsNull_AndTranscriptOmitsIt()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("OneBlock", "{}"),
            FinalMessageSse("done"));
        var runner = NewRunner(model);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(model.Requests[1], Does.Contain("{\\\"ok\\\":true}\""), "the tool output ends right after ResultJson");
            Assert.That(model.Requests[1], Does.Not.Contain("TRAILING"));
            Assert.That(record.ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(record.TrailingText, Is.Null);
            Assert.That(json, Does.Not.Contain("TrailingText"));
        });
    }

    [Test]
    public async Task SeededRecordWithTrailingText_ReachesTheModelOnTheFirstLiveRequest()
    {
        var seed = new List<AgentTranscriptTurn>
        {
            SeedTurn(1, "call_seed_1", "OneBlock", "{}", "{\"ok\":true}", "seed-trailing-NOTE"),
        };
        var model = new ScriptedHandler(FinalMessageSse("done"));
        var runner = NewRunner(model);

        var result = await runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken, seedTurns: seed);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(model.Requests, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(model.Requests[0], Does.Contain("{\\\"ok\\\":true}\\n\\nseed-trailing-NOTE"));
        });
    }

    private ModelAgentRunner NewRunner(ScriptedHandler model)
    {
        LlmOptions.Configure(["--llm-model", "trailingblock-test-model"]);
        var httpClient = new HttpClient(model)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, "trailingblock-test-model", NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist stays off so the only tool-message content is the tool output under test.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    private static AgentTranscriptTurn SeedTurn(
        int turnNumber, string callId, string tool, string argumentsJson, string resultJson, string trailingText) => new()
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
                TrailingText = trailingText,
                IsError = false,
                Latency = TimeSpan.FromMilliseconds(5),
                StartedAt = DateTimeOffset.Now,
                CompletedAt = DateTimeOffset.Now,
                ToolCallId = callId,
            },
        ],
    };

    /// <summary>One response.completed event whose output is a single function_call (a fresh call id each time).</summary>
    private static string BuildFunctionCallSse(string toolName, string argumentsJson)
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

    /// <summary>One response.completed event carrying a plain assistant message and no tool call.</summary>
    private static string FinalMessageSse(string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            response = new
            {
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[] { new { type = "output_text", text } },
                    },
                },
            },
        });

        return $"event: response.completed\ndata: {payload}\n\n";
    }

    /// <summary>Serves the scripted responses in order (the last one repeats) and records every request body.</summary>
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(next, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Test-only MCP tools with a known result shape: one returns two text blocks, one returns one.</summary>
    [McpServerToolType]
    public sealed class TrailingBlockTestTools
    {
        [McpServerTool(Name = "TwoBlocks"), System.ComponentModel.Description("Test tool: first block is the JSON result, second is a trailing note.")]
        public static CallToolResult TwoBlocks() => new()
        {
            Content =
            [
                new TextContentBlock { Text = "{\"ok\":true}" },
                new TextContentBlock { Text = "TRAILING NOTE" },
            ],
        };

        [McpServerTool(Name = "OneBlock"), System.ComponentModel.Description("Test tool: a single JSON text block.")]
        public static CallToolResult OneBlock() => new()
        {
            Content = [new TextContentBlock { Text = "{\"ok\":true}" }],
        };
    }
}
