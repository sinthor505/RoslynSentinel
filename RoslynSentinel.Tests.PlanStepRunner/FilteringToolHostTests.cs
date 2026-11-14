using System.Text.Json;
using ModelContextProtocol.Protocol;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.PlanStepRunner;

/// <summary>
/// Covers <see cref="FilteringToolHost"/>: the model-side allow-list that limits what the PlanStepRunner model
/// sees and may call. A disallowed call must never reach the server, and its error must name the tools the model
/// can call instead, stated as facts only.
/// </summary>
[TestFixture]
public class FilteringToolHostTests
{
    private static readonly IReadOnlyDictionary<string, object?> SampleArguments =
        new Dictionary<string, object?> { ["path"] = "Foo.cs" };

    [Test]
    public async Task ListTools_ReturnsOnlyAllowedTools()
    {
        var inner = new RecordingToolHost("ReadFile", "Git", "LoadSolution", "Build");
        var filtered = new FilteringToolHost(inner, new[] { "ReadFile", "Build" });

        var tools = await filtered.ListToolsAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(tools.Select(t => t.Name).ToList(), Is.EqualTo(new[] { "ReadFile", "Build" }));
    }

    [Test]
    public async Task Call_AllowedTool_PassesThroughToInner()
    {
        var inner = new RecordingToolHost("ReadFile", "Git");
        var filtered = new FilteringToolHost(inner, new[] { "ReadFile" });

        var result = await filtered.CallToolAsync("ReadFile", SampleArguments, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(inner.Result), "An allowed call must return the inner host's result unchanged.");
            Assert.That(inner.Calls, Has.Count.EqualTo(1));
            Assert.That(inner.Calls[0].Name, Is.EqualTo("ReadFile"));
            Assert.That(inner.Calls[0].Arguments, Is.SameAs(SampleArguments));
        });
    }

    [Test]
    public async Task Call_DisallowedTool_ReturnsErrorNamingCallableTools_WithoutCallingInner()
    {
        var inner = new RecordingToolHost("ReadFile", "Git", "LoadSolution", "Build");
        var filtered = new FilteringToolHost(inner, new[] { "ReadFile", "Build" });

        var result = await filtered.CallToolAsync("LoadSolution", SampleArguments, TestContext.CurrentContext.CancellationToken);
        var body = ParseErrorBody(result);
        var message = body.GetProperty("message").GetString() ?? string.Empty;

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("ToolNotAvailable"));
            Assert.That(message, Does.Contain("'LoadSolution'"));
            Assert.That(message, Does.Contain("ReadFile"));
            Assert.That(message, Does.Contain("Build"));
            Assert.That(inner.Calls, Is.Empty, "A disallowed call must never reach the inner host.");
        });
    }

    [Test]
    public async Task Call_DisallowedTool_ErrorTextIsFactsOnly()
    {
        var filtered = new FilteringToolHost(new RecordingToolHost("LoadSolution", "Build"), new[] { "Build" });

        var result = await filtered.CallToolAsync("LoadSolution", SampleArguments, TestContext.CurrentContext.CancellationToken);
        var text = ((TextContentBlock)result.Content[0]).Text.ToLowerInvariant();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("must"));
            Assert.That(text, Does.Not.Contain("do not"));
            Assert.That(text, Does.Not.Contain("never"));
            Assert.That(text, Does.Not.Contain("stop"));
        });
    }

    [Test]
    public void PlanStepRunnerSurface_IsLeanPlusGetDiagnostics()
    {
        var surface = AgentToolAllowLists.PlanStepRunnerSurface;

        Assert.Multiple(() =>
        {
            Assert.That(surface, Is.SupersetOf(AgentToolAllowLists.Lean));
            Assert.That(surface, Does.Contain("GetDiagnostics"));
            Assert.That(surface, Has.Count.EqualTo(AgentToolAllowLists.Lean.Count + 1));
            Assert.That(AgentToolAllowLists.Lean, Has.Count.EqualTo(18));
        });
    }

    /// <summary>Parses the JSON body of a ToolNotAvailable result. The message is decoded, so its quotes are plain.</summary>
    private static JsonElement ParseErrorBody(CallToolResult result)
    {
        Assert.That(result.Content, Has.Count.EqualTo(1));
        var text = ((TextContentBlock)result.Content[0]).Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Fake inner host: lists a fixed tool set and records every call it receives.</summary>
    private sealed class RecordingToolHost : IAgentToolHost
    {
        private readonly List<AgentToolDefinition> _tools;

        public RecordingToolHost(params string[] toolNames)
        {
            _tools = toolNames.Select(name => new AgentToolDefinition
            {
                Name = name,
                ParametersSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            }).ToList();
        }

        public CallToolResult Result { get; } = new() { Content = [new TextContentBlock { Text = "inner-result" }] };

        public List<(string Name, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];

        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentToolDefinition>>(_tools);

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add((toolName, arguments));
            return Task.FromResult(Result);
        }
    }
}
