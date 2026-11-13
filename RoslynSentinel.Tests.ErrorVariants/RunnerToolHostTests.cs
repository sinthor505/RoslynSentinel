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

    private static ModelAgentRunner NewRunner(ScriptedModelHandler model, IAgentToolHost host)
    {
        LlmOptions.Configure(["--llm-model", "runnertoolhost-test-model"]);
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
            turnCap: 5,
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
}
