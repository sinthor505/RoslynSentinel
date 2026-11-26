using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// The model-facing tool output follows the requested <see cref="ToolResultFormat"/>: <see cref="ModelAgentRunner"/> renders
/// the first block with <see cref="ToolResultRenderers"/>, keeps <c>ResultJson</c> as the raw JSON, and records the length of
/// the text it sent in <see cref="AgentToolCallRecord.ModelFacingChars"/>. Scripted model, in-process MCP server with a
/// test-only tool; no LM Studio needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolResultFormatRunnerTests
{
    private const string RawJson = "{\"ok\":true}";

    private ModelEvalFakeModelEnvironment _environment = null!;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _environment = await ModelEvalFakeModelEnvironment.CreateAsync(
            TestContext.CurrentContext.CancellationToken,
            runDirectoryPrefix: "rs-toolresultformat-",
            configureToolSurface: (mcpBuilder, services) => mcpBuilder.WithTools<FormatTestTools>());
        _mcpClient = _environment.Client;
        _runDirectory = _environment.RunDirectory!;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
    }

    [Test]
    public async Task Plain_ToolOutputIsRendered_ResultJsonUnchanged()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("FormatOneBlock", "{}"),
            FinalMessageSse("done"));
        var runner = NewRunner(model, ToolResultFormat.Plain);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(ToolOutput(model.Requests[1]), Is.EqualTo(ToolResultRenderers.For(ToolResultFormat.Plain).Render(RawJson)), "the tool output is the plain rendering of the first block");
            Assert.That(record.ResultJson, Is.EqualTo(RawJson), "the transcript keeps the raw JSON");
        });
    }

    [Test]
    public async Task Xml_ToolOutputIsRendered()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("FormatOneBlock", "{}"),
            FinalMessageSse("done"));
        var runner = NewRunner(model, ToolResultFormat.Xml);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var expected = ToolResultRenderers.For(ToolResultFormat.Xml).Render(RawJson);
        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(expected, Is.Not.EqualTo(RawJson), "the xml renderer changes the text, so the test would notice a pass-through");
            Assert.That(ToolOutput(model.Requests[1]), Is.EqualTo(expected), "the tool output is the xml rendering of the first block");
            Assert.That(result.Transcript.Turns[0].ToolCalls.Single().ResultJson, Is.EqualTo(RawJson));
        });
    }

    [Test]
    public async Task Default_ToolOutputIsPlainRendering()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("FormatOneBlock", "{}"),
            FinalMessageSse("done"));

        // No format argument: the runner resolves the format from the environment when it is constructed, so the
        // variable is cleared for construction only and restored straight after.
        var previous = Environment.GetEnvironmentVariable(ToolResultFormatOptions.FormatVariable);
        ModelAgentRunner runner;
        Environment.SetEnvironmentVariable(ToolResultFormatOptions.FormatVariable, null);
        try
        {
            runner = NewRunner(model, format: null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToolResultFormatOptions.FormatVariable, previous);
        }

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(ToolResultRenderers.For(ToolResultFormat.Plain).Render(RawJson), Is.Not.EqualTo(RawJson), "the plain renderer changes the text, so the test would notice a pass-through");
            Assert.That(ToolOutput(model.Requests[1]), Is.EqualTo(ToolResultRenderers.For(ToolResultFormat.Plain).Render(RawJson)), "with no format set the tool output is the plain rendering");
            Assert.That(result.Transcript.ToolResultFormat, Is.EqualTo("plain"));
        });
    }

    [Test]
    public async Task Transcript_RecordsFormatAndModelFacingChars()
    {
        var model = new ScriptedHandler(
            BuildFunctionCallSse("FormatOneBlock", "{}"),
            FinalMessageSse("done"));
        var runner = NewRunner(model, ToolResultFormat.Plain);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        var json = await File.ReadAllTextAsync(result.TranscriptPath);
        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(result.Transcript.ToolResultFormat, Is.EqualTo("plain"));
            Assert.That(record.ModelFacingChars, Is.EqualTo(ToolOutput(model.Requests[1]).Length), "ModelFacingChars is the length of the text the model was sent");
            Assert.That(json, Does.Contain("ToolResultFormat"));
        });
    }

    [Test]
    public async Task SeededRecord_RendersOnResume()
    {
        var seed = new List<AgentTranscriptTurn>
        {
            SeedTurn(1, "call_seed_1", "FormatOneBlock", "{}", RawJson, "seed-trailing-NOTE"),
        };
        var model = new ScriptedHandler(FinalMessageSse("done"));
        var runner = NewRunner(model, ToolResultFormat.Plain);

        var result = await runner.RunAsync(
            "system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken, seedTurns: seed);

        var expected = ToolResultRenderers.For(ToolResultFormat.Plain).Render(RawJson) + "\n\nseed-trailing-NOTE";
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(model.Requests, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(ToolOutput(model.Requests[0]), Is.EqualTo(expected), "the seeded record is rendered with the run's format on the first live request");
        });
    }

    private ModelAgentRunner NewRunner(ScriptedHandler model, ToolResultFormat? format)
    {
        LlmOptions.Configure(["--llm-model", "toolresultformat-test-model"]);
        var httpClient = new HttpClient(model)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, "toolresultformat-test-model", NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist stays off so the only tool-message content is the tool output under test.
        return new ModelAgentRunner(
            agentClient, _mcpClient,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 5,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off,
            toolResultFormat: format);
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

    /// <summary>The output of the single function_call_output item in a recorded request body: the tool result the model read.</summary>
    private static string ToolOutput(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("input")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
            .Single()
            .GetProperty("output")
            .GetString()!;
    }

    /// <summary>Serves the scripted responses in order (the last one repeats) and records every request body.</summary>
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The client's loaded-model status check is a GET sent before the first POST: answer it without recording it
            // or consuming a scripted response. Only POSTs are model requests.
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(next, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Test-only MCP tool with a single JSON text block.</summary>
    [McpServerToolType]
    public sealed class FormatTestTools
    {
        [McpServerTool(Name = "FormatOneBlock"), System.ComponentModel.Description("Test tool: a single JSON text block.")]
        public static CallToolResult FormatOneBlock() => new()
        {
            Content = [new TextContentBlock { Text = RawJson }],
        };
    }
}
