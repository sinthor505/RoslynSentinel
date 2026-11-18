using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// The tool message the model reads is built from the tool host's raw result: the first block is
/// <c>ResultJson</c>, the trailing blocks are appended after a blank line, and <c>IsError</c> is carried through.
/// Scripted model and an in-memory <see cref="IAgentToolHost"/>; no LM Studio and no MCP server needed.
/// </summary>
[TestFixture]
// LlmOptions.Configure is global state, so these fixtures must not run in parallel with each other.
[NonParallelizable]
public class RunnerToolHostTests
{
    private string _runDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "rs-errvariants-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    [Test]
    public async Task TwoBlocks_ToolMessageIsFirstBlankLineSecond()
    {
        var host = new FakeToolHost(
            "TwoBlocks",
            new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = "{\"ok\":true}" },
                    new TextContentBlock { Text = "TRAILING NOTE" },
                ],
            });
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("TwoBlocks", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(ScriptedModelHandler.ToolOutput(model.Requests[1]), Is.EqualTo("{\"ok\":true}\n\nTRAILING NOTE"), "the tool output is the first block, a blank line, then the trailing block");
            Assert.That(record.ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(record.TrailingText, Is.EqualTo("TRAILING NOTE"));
            Assert.That(host.LastToolName, Is.EqualTo("TwoBlocks"));
        });
    }

    [Test]
    public async Task OneBlock_ToolMessageIsTheBlockExactly()
    {
        var host = new FakeToolHost(
            "OneBlock",
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"ok\":true}" }],
            });
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("OneBlock", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Has.Count.EqualTo(2));
            Assert.That(ScriptedModelHandler.ToolOutput(model.Requests[1]), Is.EqualTo("{\"ok\":true}"), "the tool output is ResultJson exactly");
            Assert.That(record.ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(record.TrailingText, Is.Null);
        });
    }

    [Test]
    public async Task ResultWithMeta_IsRecordedOnTheCall_AndNeverShownToTheModel()
    {
        var host = new FakeToolHost(
            "WithMeta",
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"ok\":true}" }],
                Meta = new System.Text.Json.Nodes.JsonObject
                {
                    ["nearMissSubstitutions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["from"] = "Oovernight" }),
                },
            });
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("WithMeta", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.Meta?["nearMissSubstitutions"]?[0]?["from"]?.GetValue<string>(), Is.EqualTo("Oovernight"), "the call record keeps the server's _meta");
            Assert.That(ScriptedModelHandler.ToolOutput(model.Requests[1]), Is.EqualTo("{\"ok\":true}"), "the model-facing tool message is the content only");
            Assert.That(JsonSerializer.Serialize(model.Requests[1]), Does.Not.Contain("Oovernight"), "the wrong name never reaches the model request");
        });
    }

    [Test]
    public async Task ErrorResult_IsErrorTrue_IsRecorded()
    {
        var host = new FakeToolHost(
            "Failing",
            new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "{\"error\":\"not found\"}" }],
            });
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Failing", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var record = result.Transcript.Turns[0].ToolCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.IsError, Is.True);
            Assert.That(record.ResultJson, Is.EqualTo("{\"error\":\"not found\"}"));
        });
    }

    [Test]
    public async Task UnlistedCall_ReturnsToolNotAvailable_AndRunContinues()
    {
        var inner = new FakeToolHost("Listed", new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] });
        var host = new FilteringToolHost(inner, ["Listed"]);
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Hidden", "{}"),
            ScriptedModelHandler.FunctionCallSse("Listed", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(result.Transcript.Turns[0].ToolCalls.Single().IsError, Is.True);
            Assert.That(model.Requests, Has.Count.EqualTo(3));
            Assert.That(ScriptedModelHandler.ToolOutput(model.Requests[1]), Does.Contain("ToolNotAvailable").And.Contain("Listed"), "the unlisted call's result names the callable tools");
            Assert.That(inner.LastToolName, Is.EqualTo("Listed"), "the unlisted call never reaches the inner host");
        });
    }

    [Test]
    public async Task ListedCall_IsUnaffectedByTheFilter()
    {
        var inner = new FakeToolHost("Listed", new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] });
        var host = new FilteringToolHost(inner, ["Listed"]);
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Listed", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished));
            Assert.That(ScriptedModelHandler.ToolOutput(model.Requests[1]), Is.EqualTo("{\"ok\":true}"));
            Assert.That(result.Transcript.Turns[0].ToolCalls.Single().IsError, Is.False);
            Assert.That(inner.LastToolName, Is.EqualTo("Listed"));
        });
    }

    [Test]
    public async Task KeepsCallingUnavailableTools_EndsWithUnknownToolRequested()
    {
        var inner = new FakeToolHost("Listed", new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] });
        var host = new FilteringToolHost(inner, ["Listed"]);
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Hidden1", "{}"),
            ScriptedModelHandler.FunctionCallSse("Hidden2", "{}"),
            ScriptedModelHandler.FunctionCallSse("Hidden3", "{}"),
            ScriptedModelHandler.FinalMessageSse("must never be requested"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var calls = result.Transcript.Turns.SelectMany(turn => turn.ToolCalls).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.UnknownToolRequested));
            Assert.That(model.Requests, Has.Count.EqualTo(ModelAgentRunner.ConsecutiveUnavailableToolLimit), "the final message is never requested");
            Assert.That(calls, Has.Count.EqualTo(ModelAgentRunner.ConsecutiveUnavailableToolLimit));
            Assert.That(calls.All(call => call.IsError), Is.True, "every unlisted call got the host's error result");
        });
    }

    [Test]
    public async Task ListedCallResetsTheUnavailableCount()
    {
        var inner = new FakeToolHost("Listed", new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] });
        var host = new FilteringToolHost(inner, ["Listed"]);
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Hidden1", "{}"),
            ScriptedModelHandler.FunctionCallSse("Hidden2", "{}"),
            ScriptedModelHandler.FunctionCallSse("Listed", "{}"),
            ScriptedModelHandler.FunctionCallSse("Hidden3", "{}"),
            ScriptedModelHandler.FunctionCallSse("Hidden4", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host, turnCap: 8);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.StopReason, Is.EqualTo(AgentStopReason.ModelFinished), "two unlisted calls, a listed call, then two more unlisted calls never reach the limit of three in a row");
    }

    [Test]
    public async Task NormalRun_WritesSummaryAsLastBlock()
    {
        var host = new FakeToolHost(
            "Summarised",
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"ok\":true}" }],
            });
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Summarised", "{}"),
            ScriptedModelHandler.FinalMessageSse("done"));
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        using var document = JsonDocument.Parse(File.ReadAllText(result.TranscriptPath));
        Assert.Multiple(() =>
        {
            Assert.That(result.Transcript.Summary, Is.Not.Null);
            Assert.That(result.Transcript.Summary!.TurnCount, Is.EqualTo(2));
            Assert.That(result.Transcript.Summary.ToolCallCount, Is.EqualTo(1));
            Assert.That(document.RootElement.EnumerateObject().Last().Name, Is.EqualTo("Summary"), "Summary is the last top-level block of transcript.json");
        });
    }

    [Test]
    public async Task CancelledMidTurn_StillWritesTranscriptWithCancelledStopReason()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new CancellingToolHost("Cancelling", cancellation);
        var model = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("Cancelling", "{}"),
            ScriptedModelHandler.FinalMessageSse("must never be requested"));
        var runner = NewRunner(model, host);

        await Assert.CatchAsync<OperationCanceledException>(() => runner.RunAsync("system", "user", _runDirectory, cancellation.Token));

        var transcriptPath = Path.Combine(_runDirectory, "transcript.json");
        Assert.That(File.Exists(transcriptPath), Is.True, "the cancelled run still leaves transcript.json behind");
        using var document = JsonDocument.Parse(File.ReadAllText(transcriptPath));
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("StopReason").GetInt32(), Is.EqualTo((int)AgentStopReason.Cancelled));
            Assert.That(document.RootElement.TryGetProperty("Summary", out _), Is.True, "the abnormal end still carries the Summary block");
        });
    }

    [Test]
    public async Task Settings_RecordReportedSamplerAndLoadConfig()
    {
        var host = new FakeToolHost(
            "Unused",
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{}" }],
            });
        var model = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("done", 0.7, 0.9, 0.0, 0.0))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJsonWithConfig("runnertoolhost-test-model", 70144, true, true, 1),
        };
        var runner = NewRunner(model, host);

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var settings = result.Transcript.Settings ?? throw new InvalidOperationException("Transcript.Settings was not written");
        using var document = JsonDocument.Parse(File.ReadAllText(result.TranscriptPath));
        var topLevelNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(settings!.Sampler.Temperature!.Value, Is.EqualTo(0.7));
            Assert.That(settings.Sampler.Temperature.Verified, Is.True);
            Assert.That(settings.Sampler.Temperature.Source, Is.EqualTo(SamplerValue.SourceReported));
            Assert.That(settings.Sampler.TopP!.Value, Is.EqualTo(0.9));
            Assert.That(settings.Sampler.TopP.Source, Is.EqualTo(SamplerValue.SourceReported));
            Assert.That(settings.Sampler.PresencePenalty!.Value, Is.EqualTo(0.0));
            Assert.That(settings.Sampler.PresencePenalty.Source, Is.EqualTo(SamplerValue.SourceReported));
            Assert.That(settings.Sampler.FrequencyPenalty!.Value, Is.EqualTo(0.0));
            Assert.That(settings.Sampler.FrequencyPenalty.Source, Is.EqualTo(SamplerValue.SourceReported));
            Assert.That(settings.Sampler.TopK, Is.Null);
            Assert.That(settings.Load!.ContextLength, Is.EqualTo(70144));
            Assert.That(settings.Load.FlashAttention, Is.True);
            Assert.That(settings.Load.SpeculativeDraftMtp, Is.True);
            Assert.That(settings.Load.Parallel, Is.EqualTo(1));
            Assert.That(settings.Load.BaseUrl, Is.EqualTo("http://localhost/v1"));
            Assert.That(topLevelNames.IndexOf("Settings"), Is.EqualTo(topLevelNames.IndexOf("ContextLength") + 1), "Settings follows ContextLength in transcript.json");
            Assert.That(topLevelNames.IndexOf("Turns"), Is.GreaterThan(topLevelNames.IndexOf("Settings")), "Settings precedes Turns in transcript.json");
        });
    }

    [Test]
    public async Task Settings_RecordDeclaredHostSamplerAsUnverified()
    {
        var host = new FakeToolHost(
            "Unused",
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{}" }],
            });
        var model = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("done", 0.7, 0.9, 0.0, 0.0));
        var runner = NewRunner(model, host, hostSampler: "topK=20;minP=0.1");

        var result = await runner.RunAsync("system", "user", _runDirectory, TestContext.CurrentContext.CancellationToken);

        var settings = result.Transcript.Settings;
        Assert.That(settings, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(settings!.Sampler.TopK!.Value, Is.EqualTo(20));
            Assert.That(settings.Sampler.TopK.Verified, Is.False);
            Assert.That(settings.Sampler.TopK.Source, Is.EqualTo(SamplerValue.SourceDeclared));
            Assert.That(settings.Sampler.MinP!.Value, Is.EqualTo(0.1));
            Assert.That(settings.Sampler.MinP.Verified, Is.False);
            Assert.That(settings.Sampler.MinP.Source, Is.EqualTo(SamplerValue.SourceDeclared));
            Assert.That(settings.Sampler.RepeatPenalty, Is.Null);
        });
    }

    private static ModelAgentRunner NewRunner(ScriptedModelHandler model, IAgentToolHost host, int turnCap = 5, string? hostSampler = null)
    {
        if (hostSampler is null)
        {
            LlmOptions.Configure(["--llm-model", "runnertoolhost-test-model"]);
        }
        else
        {
            LlmOptions.Configure(["--llm-model", "runnertoolhost-test-model", "--llm-host-sampler=" + hostSampler]);
        }
        var httpClient = new HttpClient(model)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        var agentClient = new LmStudioAgentClient(httpClient, "runnertoolhost-test-model", NullLogger<LmStudioAgentClient>.Instance);

        // Loop assist stays off so the only tool-message content is the tool output under test.
        return new ModelAgentRunner(
            agentClient, host,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 8192,
            turnCap: turnCap,
            wallClockCap: TimeSpan.FromMinutes(2),
            logger: new NullLogger<ModelAgentRunner>(),
            loopAssist: LoopAssistOptions.Off);
    }

    /// <summary>In-memory tool host: lists one tool and returns a fixed raw result, recording the last call it saw.</summary>
    private sealed class FakeToolHost(string toolName, CallToolResult result) : IAgentToolHost
    {
        public string? LastToolName { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<AgentToolDefinition> tools =
            [
                new AgentToolDefinition
                {
                    Name = toolName,
                    Description = "Test tool with a fixed raw result.",
                    ParametersSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
                },
            ];
            return Task.FromResult(tools);
        }

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            LastToolName = toolName;
            LastArguments = arguments;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Lists one tool; calling it cancels the run's token and then returns a normal result, so the loop's next
    /// awaited step is the one that observes the cancellation.
    /// </summary>
    private sealed class CancellingToolHost(string toolName, CancellationTokenSource cancellation) : IAgentToolHost
    {
        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<AgentToolDefinition> tools =
            [
                new AgentToolDefinition
                {
                    Name = toolName,
                    Description = "Test tool that cancels the run when called.",
                    ParametersSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
                },
            ];
            return Task.FromResult(tools);
        }

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"ok\":true}" }],
            });
        }
    }
}
