using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ModelAgentRunner"/> thinking decisions (<see cref="ThinkingOptions"/>) against a scripted model and a fake
/// tool host. Checks which chat requests carry the top-level "reasoning" property and which transcript turns record a
/// thinking decision. No LM Studio and no MCP server needed.
/// </summary>
// Intentional deviation: [NonParallelizable] because this class mutates the static ModelAgentRunner.ThinkingDefault;
// later tests in this file will too.
[TestFixture]
[NonParallelizable]
public class ThinkingRunnerTests
{
    private const string ModelId = "thinking-test-model";
    private const string ToolName = "McpServerStatus";

    // Markup is built by concatenation: the server's edit guard rejects literal tool-call markup in newContent.
    private const string ToolCallOpen = "<" + "tool_call>";
    private const string ToolCallClose = "<" + "/tool_call>";
    private const string ParameterOpen = "<" + "parameter=reason>";

    private string _transcriptDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _transcriptDirectory = Path.Combine(ScratchRoot.TestRunsRoot(), "ThinkingRunnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcriptDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_transcriptDirectory))
        {
            Directory.Delete(_transcriptDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Triggered_Error_ThinksOnlyAfterErrorTurn()
    {
        // Turn 1 errors, turn 2 succeeds, turn 3 is the final text. Only the request after the error carries thinking.
        var harness = NewRunner(
            Triggered(ThinkingTriggers.Error),
            [ToolCallTurn(), ToolCallTurn(), FinalTurn()],
            (_, index) => index == 0 ? ErrorResult() : SuccessResult());

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { false, true, false }));
            Assert.That(result.Transcript.Turns[1].Thinking?.Requested, Is.True);
            Assert.That(result.Transcript.Turns[1].Thinking?.FiredTriggers, Is.EqualTo(ThinkingTriggers.Error));
        });
    }

    [Test]
    public async Task Triggered_LatchRefusal_ThinksAfterRefusal()
    {
        // Run A: turn 1's result is a latch refusal, so the request after it carries thinking.
        var refusalRun = NewRunner(
            Triggered(ThinkingTriggers.LatchRefusal),
            [ToolCallTurn(), FinalTurn()],
            (toolName, index) => index == 0 ? LatchRefusalResult(toolName) : SuccessResult());

        var refusalResult = await RunAsync(refusalRun);

        // Run B: a plain error is not a latch refusal, so no request carries thinking.
        var errorRun = NewRunner(
            Triggered(ThinkingTriggers.LatchRefusal),
            [ToolCallTurn(), FinalTurn()],
            (_, _) => ErrorResult());

        await RunAsync(errorRun);

        Assert.Multiple(() =>
        {
            Assert.That(ReasoningFlags(refusalRun.Model), Is.EqualTo(new[] { false, true }));
            Assert.That(refusalResult.Transcript.Turns[1].Thinking?.Requested, Is.True);
            Assert.That(refusalResult.Transcript.Turns[1].Thinking?.FiredTriggers, Is.EqualTo(ThinkingTriggers.LatchRefusal));
            Assert.That(ReasoningFlags(errorRun.Model), Is.EqualTo(new[] { false, false }));
        });
    }

    [Test]
    public async Task Triggered_FirstTurn_ThinksOnFirstRequestOnly()
    {
        var harness = NewRunner(
            Triggered(ThinkingTriggers.FirstTurn),
            [ToolCallTurn(), ToolCallTurn(), FinalTurn()]);

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { true, false, false }));
            Assert.That(result.Transcript.Turns[0].Thinking?.FiredTriggers, Is.EqualTo(ThinkingTriggers.FirstTurn));
        });
    }

    [Test]
    public async Task Always_ThinksOnEveryRequest()
    {
        var harness = NewRunner(
            new ThinkingOptions(ThinkingMode.Always, ThinkingTriggers.Error, ThinkingOptions.DefaultEffort, null),
            [ToolCallTurn(), ToolCallTurn(), FinalTurn()]);

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { true, true, true }));
            Assert.That(result.Transcript.Turns.All(turn => turn.Thinking?.Requested == true), Is.True);
        });
    }

    [Test]
    public async Task Off_NoReasoning_HeaderStamped()
    {
        var harness = NewRunner(
            ThinkingOptions.Off,
            [ToolCallTurn(), FinalTurn()]);

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { false, false }));
            Assert.That(result.Transcript.Thinking?.Mode, Is.EqualTo(ThinkingMode.Off));
            Assert.That(result.Transcript.Turns.All(turn => turn.Thinking is null), Is.True);
        });
    }

    [Test]
    public async Task Triggered_Error_SkipsNudgeTurn()
    {
        // Turn 1 errors. Turn 2 has tool-call markup as text but no tool call, which gets the malformed-markup nudge.
        // Turn 3 is the final text. The error fires thinking on turn 2's request only; the nudge turn adds no trigger.
        var harness = NewRunner(
            Triggered(ThinkingTriggers.Error),
            [ToolCallTurn(), MarkupTurn(), FinalTurn()],
            (_, _) => ErrorResult());

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { false, true, false }));
            Assert.That(result.Transcript.Turns[1].Thinking?.FiredTriggers, Is.EqualTo(ThinkingTriggers.Error));
        });
    }

    /// <summary>Thinking settings for the Triggered mode with the given triggers and the default effort.</summary>
    private static ThinkingOptions Triggered(ThinkingTriggers triggers) =>
        new(ThinkingMode.Triggered, triggers, ThinkingOptions.DefaultEffort, null);

    /// <summary>One scripted model turn that makes a <see cref="ToolName"/> call with arguments distinct from every other call.</summary>
    private static ScriptedTurn ToolCallTurn(int reasoningDeltas = 1) => new(ReasoningDeltas: reasoningDeltas, CallTool: true);

    /// <summary>One scripted model turn that replies with plain text and no tool call, which finishes the run.</summary>
    private static ScriptedTurn FinalTurn(string text = "Done.") => new(Content: text);

    /// <summary>One scripted model turn whose text holds tool-call markup but no parsed tool call.</summary>
    private static ScriptedTurn MarkupTurn() => new(Content: ToolCallOpen + ParameterOpen + "replay" + ToolCallClose);

    private static CallToolResult SuccessResult() => TextResult("{\"ok\":true}", isError: false);

    private static CallToolResult ErrorResult() =>
        TextResult("{\"errorData\":{\"errorCode\":\"ExecutionFailed\",\"message\":\"simulated failure\"}}", isError: true);

    /// <summary>The latch refusal body shape that <see cref="ToolLoopLatch.RefusalMessage"/> recognises.</summary>
    private static CallToolResult LatchRefusalResult(string toolName) =>
        TextResult(
            JsonSerializer.Serialize(new
            {
                errorData = new
                {
                    errorCode = "TargetIneligible",
                    message = toolName + ToolLoopLatch.RefusalMarker + ": it failed the same way 3 times.",
                },
            }),
            isError: true);

    private static CallToolResult TextResult(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };

    private static Harness NewRunner(
        ThinkingOptions? thinking,
        IReadOnlyList<ScriptedTurn> script,
        Func<string, int, CallToolResult>? respond = null,
        ILogger<ModelAgentRunner>? logger = null)
    {
        LlmOptions.Configure(["--llm-model", ModelId]);
        var model = new ScriptedModelHandler(script);
        var httpClient = new HttpClient(model)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, ModelId, NullLogger<LmStudioAgentClient>.Instance);
        var tools = new ScriptedToolHost(respond ?? ((_, _) => SuccessResult()));

        // The breaker is out of the way (limit 10), and loop assist is off so no injected notes change the requests.
        var runner = new ModelAgentRunner(
            agentClient,
            tools,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: 24,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: logger ?? NullLogger<ModelAgentRunner>.Instance,
            loopAssist: LoopAssistOptions.Off,
            thinking: thinking);
        return new Harness(runner, model, tools);
    }

    private Task<AgentRunResult> RunAsync(Harness harness) =>
        harness.Runner.RunAsync("system", "user", _transcriptDirectory, TestContext.CurrentContext.CancellationToken);

    /// <summary>Whether each recorded chat request carried a top-level "reasoning" property, in request order.</summary>
    private static List<bool> ReasoningFlags(ScriptedModelHandler model) =>
        model.RequestBodies.Select(body =>
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("reasoning", out _);
        }).ToList();

    /// <summary>One scripted model turn: how many reasoning deltas to stream, the message content, and whether it calls the tool.</summary>
    private sealed record ScriptedTurn(int ReasoningDeltas = 0, string? Content = null, bool CallTool = false);

    /// <summary>The runner, its scripted model and its fake tool host, so a test can read the recorded requests and calls.</summary>
    private sealed record Harness(ModelAgentRunner Runner, ScriptedModelHandler Model, ScriptedToolHost Tools);

    /// <summary>
    /// Answers the models endpoint with 404 (context length unknown) and each chat request with the next scripted turn,
    /// then with plain "Done." once the script runs out. Records every chat request body.
    /// </summary>
    private sealed class ScriptedModelHandler(IReadOnlyList<ScriptedTurn> script) : HttpMessageHandler
    {
        private int _chatCalls;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var index = _chatCalls++;
            var turn = index < script.Count ? script[index] : new ScriptedTurn(Content: "Done.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildStream(turn, index), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string BuildStream(ScriptedTurn turn, int index)
        {
            var sb = new StringBuilder();
            for (var k = 1; k <= turn.ReasoningDeltas; k++)
            {
                var delta = JsonSerializer.Serialize(new { item_id = "rs_" + index, delta = "thinking-" + index + "-" + k });
                sb.Append("event: response.reasoning_text.delta\ndata: ").Append(delta).Append("\n\n");
            }

            var output = new List<object>();
            if (!string.IsNullOrEmpty(turn.Content))
            {
                output.Add(new { type = "message", content = new[] { new { type = "output_text", text = turn.Content } } });
            }

            if (turn.CallTool)
            {
                // The varying toolNameFilter keeps each call's arguments distinct, so no identical-success nudge fires.
                var arguments = JsonSerializer.Serialize(new { reason = "thinking test turn " + index, toolNameFilter = "x" + index });
                output.Add(new { type = "function_call", call_id = "call_" + index, name = ToolName, arguments });
            }

            var completed = JsonSerializer.Serialize(new { response = new { output, usage = new { total_tokens = 1000 } } });
            sb.Append("event: response.completed\ndata: ").Append(completed).Append("\n\n");
            return sb.ToString();
        }
    }

    /// <summary>
    /// In-memory tool host: lists one tool and answers each call with <c>respond(toolName, callIndex)</c>, where the call
    /// index counts calls from 0 across the run. Records every call it receives.
    /// </summary>
    private sealed class ScriptedToolHost(Func<string, int, CallToolResult> respond) : IAgentToolHost
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];

        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<AgentToolDefinition> tools =
            [
                new AgentToolDefinition
                {
                    Name = ToolName,
                    Description = "Test tool with scripted results.",
                    ParametersSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                },
            ];
            return Task.FromResult(tools);
        }

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var index = Calls.Count;
            Calls.Add((toolName, arguments));
            return Task.FromResult(respond(toolName, index));
        }
    }

    [Test]
    public void Serialize_WritesEnumNames()
    {
        var transcript = new AgentTranscript
        {
            Thinking = new ThinkingOptions(ThinkingMode.Triggered, ThinkingTriggers.Error | ThinkingTriggers.FirstTurn, "low", null),
        };

        var json = JsonSerializer.Serialize(transcript, ModelAgentRunner.TranscriptFileOptions);

        using var document = JsonDocument.Parse(json);
        var thinking = document.RootElement.GetProperty("Thinking");
        Assert.Multiple(() =>
        {
            Assert.That(thinking.GetProperty("Mode").GetString(), Is.EqualTo("Triggered"));
            Assert.That(thinking.GetProperty("Triggers").GetString(), Does.Contain("Error").And.Contain("FirstTurn"));
        });

        var parsed = JsonSerializer.Deserialize<AgentTranscript>(json, ModelAgentRunner.TranscriptFileOptions);
        Assert.That(parsed?.Thinking, Is.EqualTo(transcript.Thinking));
    }

    [Test]
    public async Task Triggered_HeaderEqualsOptions()
    {
        var options = new ThinkingOptions(ThinkingMode.Triggered, ThinkingTriggers.Error | ThinkingTriggers.LatchRefusal, "medium", 2000);
        var harness = NewRunner(options, [ToolCallTurn(), FinalTurn()]);

        var result = await RunAsync(harness);

        Assert.That(result.Transcript.Thinking, Is.EqualTo(options));
    }

    [Test]
    public async Task Budget_OverBudgetFlaggedNotCut()
    {
        // Turn 1 streams 3 reasoning deltas against a budget of 2: it is flagged, but the run is not cut and no extra request is made.
        var harness = NewRunner(
            new ThinkingOptions(ThinkingMode.Always, ThinkingTriggers.Error, ThinkingOptions.DefaultEffort, 2),
            [ToolCallTurn(reasoningDeltas: 3), new ScriptedTurn(ReasoningDeltas: 1, Content: "Done.")]);

        var result = await RunAsync(harness);

        Assert.Multiple(() =>
        {
            Assert.That(result.Transcript.Turns[0].Thinking?.OverBudget, Is.True);
            Assert.That(result.Transcript.Turns[0].Thinking?.ReasoningDeltas, Is.EqualTo(3));
            Assert.That(result.Transcript.Turns[1].Thinking?.OverBudget, Is.False);
            Assert.That(result.Transcript.Turns[1].Thinking?.ReasoningDeltas, Is.EqualTo(1));
            Assert.That(harness.Model.RequestBodies, Has.Count.EqualTo(2));
            Assert.That(result.Transcript.Summary?.OverBudgetTurnCount, Is.EqualTo(1));
            Assert.That(result.Transcript.Summary?.ThinkingTurnCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task CustomEffort_WarnsOnce()
    {
        var logger = new LevelListLogger();
        var harness = NewRunner(
            new ThinkingOptions(ThinkingMode.Always, ThinkingTriggers.Error, "turbo", null),
            [ToolCallTurn(), ToolCallTurn(), FinalTurn()],
            logger: logger);

        await RunAsync(harness);

        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToList();
        Assert.That(warnings, Is.EqualTo(new[] { string.Format(ThinkingOptions.CustomEffortWarningFormat, "turbo", ModelId) }));
    }

    [Test]
    public async Task CtorWithoutThinking_UsesStaticDefault()
    {
        var previous = ModelAgentRunner.ThinkingDefault;
        ModelAgentRunner.ThinkingDefault = new ThinkingOptions(ThinkingMode.Always, ThinkingTriggers.Error, ThinkingOptions.DefaultEffort, null);
        try
        {
            var harness = NewRunner(null, [FinalTurn()]);

            await RunAsync(harness);

            Assert.That(ReasoningFlags(harness.Model), Is.EqualTo(new[] { true }));
        }
        finally
        {
            ModelAgentRunner.ThinkingDefault = previous;
        }
    }

    [Test]
    public void Transcript_WithoutThinkingFields_Deserializes()
    {
        var transcript = new AgentTranscript();
        transcript.Turns.Add(new AgentTranscriptTurn
        {
            TurnNumber = 1,
            ModelMessage = new AgentChatMessage { Role = "assistant" },
            ModelLatency = TimeSpan.Zero,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var json = JsonSerializer.Serialize(transcript, ModelAgentRunner.TranscriptFileOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.TryGetProperty("Thinking", out _), Is.False);
            Assert.That(document.RootElement.GetProperty("Turns")[0].TryGetProperty("Thinking", out _), Is.False);
        });

        var parsed = JsonSerializer.Deserialize<AgentTranscript>(json, ModelAgentRunner.TranscriptFileOptions);

        // Turns is get-only, which System.Text.Json leaves empty on a plain deserialize, so the turn is read on its own.
        var turn = JsonSerializer.Deserialize<AgentTranscriptTurn>(
            document.RootElement.GetProperty("Turns")[0].GetRawText(),
            ModelAgentRunner.TranscriptFileOptions);
        Assert.Multiple(() =>
        {
            Assert.That(parsed!.Thinking, Is.Null);
            Assert.That(turn!.Thinking, Is.Null);
        });
    }

    /// <summary>Captures the level and rendered message of every log call, so a test can count warnings.</summary>
    private sealed class LevelListLogger : ILogger<ModelAgentRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
