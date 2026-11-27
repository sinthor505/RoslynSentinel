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
    private const int ToolTurnCount = 17;

    private ModelEvalFakeModelEnvironment? _environment;
    private McpClient _mcpClient = null!;
    private string _runDirectory = null!;
    private ReplayScriptedHandler _handler = null!;
    private string? _savedReplayWindow;
    private string? _savedReplayForm;

    [SetUp]
    public async Task SetUp()
    {
        _savedReplayWindow = Environment.GetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable);
        Environment.SetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable, null);
        _savedReplayForm = Environment.GetEnvironmentVariable(ReasoningReplayPolicy.FormVariable);
        Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, null);

        _environment = await ModelEvalFakeModelEnvironment.CreateAsync(
            TestContext.CurrentContext.CancellationToken, runDirectoryPrefix: "rs-reasoning-replay-");
        _mcpClient = _environment.Client;
        _runDirectory = _environment.RunDirectory!;
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (_environment is not null)
            {
                await _environment.DisposeAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable, _savedReplayWindow);
            Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, _savedReplayForm);
        }
    }

    [Test]
    public async Task PressureMode_DropsOnceAtTurnFourteen_AndNotAgainWithinTheHysteresisBand()
    {
        // Turns 1-17 make a tool call; turn 18 is the plain finish. Request index i carries the reasoning of turns 1..i+1.
        // Totals (index i = total reported after turn i+1): 1000 for turns 1-11, 4000 for turn 12, 7000 for turn 13,
        // 7500 for turn 14, 7800 for turn 15, then clamped at 7800. The request of turn 13 projects about 4000 plus a small
        // tool-result estimate, under the 7000 threshold. The request of turn 14 projects >= 7000 with 13 prior assistant
        // turns: 3 pinned + 10 replayable, so 5 are dropped (turns 4-8). The later totals grow by less than 10% of the
        // context (1000), so the hysteresis band holds and no second drop happens.
        var totals = new[] { 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 4000, 7000, 7500, 7800 };
        var runner = NewRunner(contextLength: 10000, totals, ToolTurns(ToolTurnCount));

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var bodies = _handler.RequestBodies;
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.ContextLength, Is.EqualTo(10000));
            Assert.That(result.Transcript.ReasoningReplayMode, Is.EqualTo("pressure:0.7,pinned:3"));
            Assert.That(result.Transcript.ReasoningDropTurns, Is.Not.Null);
            Assert.That(result.Transcript.ReasoningDropTurns!, Is.EqualTo(new[] { 14 }));
            Assert.That(bodies, Has.Count.EqualTo(ToolTurnCount + 1));

            // Request index 13 is turn 14: turns 4-8 are dropped; pinned turns 1-3 and turns 9-13 are kept.
            for (var k = 4; k <= 8; k++)
            {
                Assert.That(bodies[13], Does.Not.Contain(Marker(k)), $"turn {k} reasoning should be dropped");
            }

            for (var k = 1; k <= 3; k++)
            {
                Assert.That(bodies[13], Does.Contain(Marker(k)), $"pinned turn {k} reasoning should be kept");
            }

            for (var k = 9; k <= 13; k++)
            {
                Assert.That(bodies[13], Does.Contain(Marker(k)), $"turn {k} reasoning should be kept");
            }

            // Request index 12 is turn 13, before the drop: every reasoning so far is present.
            for (var k = 1; k <= 12; k++)
            {
                Assert.That(bodies[12], Does.Contain(Marker(k)), $"turn {k} reasoning should be present before the drop");
            }

            Assert.That(CountMarkers(bodies[13]), Is.EqualTo(8));
            Assert.That(CountMarkers(bodies[13]), Is.LessThan(CountMarkers(bodies[12])));

            // The dropped set only grows and nothing else drops: the last request still lacks turns 4-8 and keeps the rest.
            for (var k = 4; k <= 8; k++)
            {
                Assert.That(bodies[^1], Does.Not.Contain(Marker(k)));
            }

            for (var k = 1; k <= 3; k++)
            {
                Assert.That(bodies[^1], Does.Contain(Marker(k)));
            }

            for (var k = 9; k <= ToolTurnCount; k++)
            {
                Assert.That(bodies[^1], Does.Contain(Marker(k)), $"turn {k} reasoning should not be dropped again");
            }
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

    [TestCase(null, ReasoningReplayForm.Items)]
    [TestCase("content", ReasoningReplayForm.Content)]
    public async Task Transcript_StampsReasoningReplayForm(string? variable, ReasoningReplayForm expected)
    {
        Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, variable);
        var runner = NewRunner(contextLength: 10000, new[] { 1000 }, []);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Transcript.ReasoningReplayForm, Is.EqualTo(expected));
    }

    [TestCase(ThinkingMode.Triggered, ReasoningReplayForm.Drop)]
    [TestCase(ThinkingMode.Off, ReasoningReplayForm.Items)]
    public async Task Triggered_FormUnset_UsesFlippedForm(ThinkingMode mode, ReasoningReplayForm expected)
    {
        Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, null);
        var thinking = mode == ThinkingMode.Triggered
            ? new ThinkingOptions(ThinkingMode.Triggered, ThinkingTriggers.Error, ThinkingOptions.DefaultEffort, null)
            : new ThinkingOptions(ThinkingMode.Off, ThinkingTriggers.None, ThinkingOptions.DefaultEffort, null);
        var runner = NewRunner(contextLength: 10000, new[] { 1000 }, [], thinking);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Transcript.ReasoningReplayForm, Is.EqualTo(expected));
    }

    [Test]
    public async Task ExplicitForm_WinsOverTriggeredDefault()
    {
        Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, "items");
        var thinking = new ThinkingOptions(ThinkingMode.Triggered, ThinkingTriggers.Error, ThinkingOptions.DefaultEffort, null);
        var runner = NewRunner(contextLength: 10000, new[] { 1000 }, [], thinking);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Transcript.ReasoningReplayForm, Is.EqualTo(ReasoningReplayForm.Items));
    }

    [Test]
    public async Task ContentForm_RequestBodiesCarryReasoningAsAssistantText()
    {
        // Content form: no reasoning input item; each turn's reasoning rides as assistant message text instead.
        Environment.SetEnvironmentVariable(ReasoningReplayPolicy.FormVariable, "content");
        var runner = NewRunner(contextLength: 10000, new[] { 1000 }, ToolTurns(3));

        await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var bodies = _handler.RequestBodies;
        Assert.That(bodies, Has.Count.GreaterThanOrEqualTo(2));

        using var document = JsonDocument.Parse(bodies[^1]);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToList();
        var assistantContents = input
            .Where(item => item.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .Select(item => item.GetProperty("content").GetRawText())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                input.Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning").ToList(),
                Is.Empty,
                "content form sends no reasoning input item");
            for (var k = 1; k <= 3; k++)
            {
                Assert.That(
                    assistantContents.Any(content => content.Contains(Marker(k), StringComparison.Ordinal)),
                    Is.True,
                    $"turn {k} reasoning should appear in an assistant message item's content");
            }
        });
    }

    [Test]
    public void Transcript_WithoutReasoningReplayFields_Deserializes()
    {
        var transcript = JsonSerializer.Deserialize<AgentTranscript>("{\"RunId\":\"r1\",\"Summary\":{}}");

        Assert.That(transcript, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(transcript!.RunId, Is.EqualTo("r1"));
            Assert.That(transcript.ContextLength, Is.Null);
            Assert.That(transcript.ReasoningReplayMode, Is.Null);
            Assert.That(transcript.ReasoningDropTurns, Is.Null);
            Assert.That(transcript.ReasoningReplayForm, Is.Null);
            Assert.That(transcript.Summary, Is.Not.Null);
            Assert.That(transcript.Summary!.LeakedThinkTagTurnCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Serialize_WritesReasoningReplayFormName()
    {
        var transcript = new AgentTranscript { ReasoningReplayForm = ReasoningReplayForm.Content };

        var json = JsonSerializer.Serialize(transcript, ModelAgentRunner.TranscriptFileOptions);

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("ReasoningReplayForm").GetString(), Is.EqualTo("Content"));
        var parsed = JsonSerializer.Deserialize<AgentTranscript>(json, ModelAgentRunner.TranscriptFileOptions);
        Assert.That(parsed?.ReasoningReplayForm, Is.EqualTo(ReasoningReplayForm.Content));

        var nullJson = JsonSerializer.Serialize(new AgentTranscript { ReasoningReplayForm = null }, ModelAgentRunner.TranscriptFileOptions);
        var nullParsed = JsonSerializer.Deserialize<AgentTranscript>(nullJson, ModelAgentRunner.TranscriptFileOptions);
        Assert.That(nullParsed?.ReasoningReplayForm, Is.Null);
    }

    private static string Marker(int turn) => $"PLAN-MARKER-{turn:D2}-END";

    private static int CountMarkers(string body) => Enumerable.Range(1, ToolTurnCount).Count(k => body.Contains(Marker(k)));

    /// <summary>Turns 1..count each carry their own marker and make a tool call.</summary>
    private static List<ScriptedTurn> ToolTurns(int count) =>
        Enumerable.Range(1, count).Select(k => new ScriptedTurn(Marker(k), null, CallTool: true)).ToList();

    private ModelAgentRunner NewRunner(int? contextLength, int[] totalTokens, IReadOnlyList<ScriptedTurn> script, ThinkingOptions? thinking = null)
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
            turnCap: 24,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off,
            thinking: thinking);
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
