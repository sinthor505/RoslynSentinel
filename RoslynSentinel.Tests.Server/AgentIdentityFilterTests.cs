using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Behaviour coverage for the outermost call-tool filter that lifts the agent identity keys
/// (<c>_agentId</c>, <c>_agentType</c>) out of the arguments, latches the operating mode and sets the
/// ambient <see cref="ToolCallContext"/> for the call. Runs against a real in-process
/// <see cref="McpClient"/>/<see cref="ModelContextProtocol.Server.McpServer"/> pair so the identity keys
/// are exercised through the same filter pipeline a real call uses.
/// </summary>
// Mutates the static AgentOperatingModeOptions and the static AgentIdentityProbeTool.LastSeen capture,
// so these tests must not run in parallel with each other or with other tests that touch that state.
[TestFixture]
[NonParallelizable]
public class AgentIdentityFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    [SetUp]
    public async Task SetUp()
    {
        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());
        ToolCallEchoOptions.Configure(["--echo-tool-args=true"]);
        AgentIdentityProbeTool.LastSeen = null;
        ToolCallContext.Set(null);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        mcpBuilder.WithTools<AgentIdentityProbeTool>();

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

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();

        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());
        ToolCallContext.Set(null);
    }

    [Test]
    public async Task CallWithIdentityKeys_IsNotRefused_AndToolSeesTheContext()
    {
        var result = await CallProbeAsync(Args("abc", "implementer"));
        var text = result.Content.OfType<TextContentBlock>().First().Text;

        Assert.That(result.IsError, Is.Not.True, "A call carrying identity keys must not be refused.");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));

        var seen = AgentIdentityProbeTool.LastSeen;
        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.AgentId, Is.EqualTo("abc"));
        Assert.That(seen.AgentType, Is.EqualTo("implementer"));
        Assert.That(seen.ToolName, Is.EqualTo("AgentIdentityProbe"));
        Assert.That(seen.Reason, Is.EqualTo("test message"));
    }

    [Test]
    public async Task EchoedArguments_OmitTheIdentityKeys()
    {
        var result = await CallProbeAsync(Args("abc", "implementer"));
        var text = result.Content.OfType<TextContentBlock>().First().Text;

        var toolCall = JsonNode.Parse(text)!["toolCall"]!;
        var arguments = toolCall["arguments"]!.AsObject();

        Assert.That(arguments.ContainsKey("reason"), Is.True);
        Assert.That(arguments.ContainsKey(AgentIdentityCapture.AgentIdKey), Is.False);
        Assert.That(arguments.ContainsKey(AgentIdentityCapture.AgentTypeKey), Is.False);
    }

    [Test]
    public async Task CallWithoutKeys_HasContextWithNullIdentity()
    {
        var result = await CallProbeAsync(Args());

        Assert.That(result.IsError, Is.Not.True);

        var seen = AgentIdentityProbeTool.LastSeen;
        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.AgentId, Is.Null);
        Assert.That(seen.AgentType, Is.Null);
        Assert.That(seen.ToolName, Is.EqualTo("AgentIdentityProbe"));
    }

    [Test]
    public async Task StartSeq_IsTheEditAttributionSeqAtCallStart()
    {
        var workspaceManager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        workspaceManager.EditAttributions.Append("x.cs", null, EditAttributionOp.Write, null);
        workspaceManager.EditAttributions.Append("x.cs", null, EditAttributionOp.Write, null);

        var result = await CallProbeAsync(Args());

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(AgentIdentityProbeTool.LastSeen, Is.Not.Null);
        Assert.That(AgentIdentityProbeTool.LastSeen!.StartSeq, Is.EqualTo(2));
    }

    [Test]
    public async Task ContextDoesNotCarryOverBetweenCalls()
    {
        await CallProbeAsync(Args("abc"));
        await CallProbeAsync(Args());

        Assert.That(AgentIdentityProbeTool.LastSeen, Is.Not.Null);
        Assert.That(AgentIdentityProbeTool.LastSeen!.AgentId, Is.Null);
    }

    [Test]
    public async Task AutoMode_FirstCallWithId_LatchesMultiAgent()
    {
        await CallProbeAsync(Args("abc"));
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.MultiAgent));

        await CallProbeAsync(Args());
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.MultiAgent));
    }

    [Test]
    public async Task AutoMode_FirstCallWithoutId_LatchesSingleAgent()
    {
        await CallProbeAsync(Args());
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.SingleAgent));

        await CallProbeAsync(Args("abc"));
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.SingleAgent));
    }

    // Direct tests of the helper: no pipeline call, so the ambient context is set and cleared by the test itself.

    [Test]
    public void Enter_SeqProviderThrows_DoesNotThrow_LeavesNullContext_AndKeysAreStripped()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                [AgentIdentityCapture.AgentIdKey] = JsonDocument.Parse("\"abc\"").RootElement.Clone(),
                [AgentIdentityCapture.AgentTypeKey] = JsonDocument.Parse("\"implementer\"").RootElement.Clone(),
            };

            Assert.DoesNotThrow(() => AgentIdentityCapture.Enter("T", arguments, () => throw new InvalidOperationException("boom")));

            Assert.That(ToolCallContext.Current, Is.Null);
            Assert.That(arguments.ContainsKey(AgentIdentityCapture.AgentIdKey), Is.False);
            Assert.That(arguments.ContainsKey(AgentIdentityCapture.AgentTypeKey), Is.False);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_NonStringIdentity_IsNull_AndKeyStripped()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                [AgentIdentityCapture.AgentIdKey] = JsonDocument.Parse("5").RootElement.Clone(),
            };

            AgentIdentityCapture.Enter("T", arguments, () => 7);

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.AgentId, Is.Null);
            Assert.That(ToolCallContext.Current.StartSeq, Is.EqualTo(7));
            Assert.That(arguments.ContainsKey(AgentIdentityCapture.AgentIdKey), Is.False);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_NullArguments_SetsContextWithNullIdentity()
    {
        try
        {
            AgentIdentityCapture.Enter(null, null, () => 0);

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.ToolName, Is.EqualTo(""));
            Assert.That(ToolCallContext.Current.AgentId, Is.Null);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_SessionId_IsCapturedAndStripped()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                [AgentIdentityCapture.AgentIdKey] = JsonDocument.Parse("\"a1\"").RootElement.Clone(),
                [AgentIdentityCapture.SessionIdKey] = JsonDocument.Parse("\"s1\"").RootElement.Clone(),
            };

            AgentIdentityCapture.Enter("T", arguments, () => 0);

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.SessionId, Is.EqualTo("s1"));
            Assert.That(arguments.ContainsKey(AgentIdentityCapture.SessionIdKey), Is.False);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_MappedTool_CapturesFileAndTerm()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["filePath"] = JsonDocument.Parse("\"Foo.cs\"").RootElement.Clone(),
                ["methodName"] = JsonDocument.Parse("\"Bar\"").RootElement.Clone(),
            };

            AgentIdentityCapture.Enter("GetMethodSource", arguments, () => 0, () => "C:\\root");

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.TargetFilePath, Is.EqualTo("Foo.cs"));
            Assert.That(ToolCallContext.Current.MissingTerm, Is.EqualTo("Bar"));
            Assert.That(ToolCallContext.Current.SolutionRoot, Is.EqualTo("C:\\root"));
            Assert.That(arguments.ContainsKey("filePath"), Is.True);
            Assert.That(arguments.ContainsKey("methodName"), Is.True);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_ReplaceSnippet_TermIsOldContent()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["filePath"] = JsonDocument.Parse("\"Foo.cs\"").RootElement.Clone(),
                ["oldContent"] = JsonDocument.Parse("\"a b\"").RootElement.Clone(),
            };

            AgentIdentityCapture.Enter("ReplaceSnippet", arguments, () => 0);

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("a b"));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_UnmappedTool_HasNoTerm()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["filePath"] = JsonDocument.Parse("\"Foo.cs\"").RootElement.Clone(),
                ["methodName"] = JsonDocument.Parse("\"Bar\"").RootElement.Clone(),
            };

            AgentIdentityCapture.Enter("Build", arguments, () => 0, () => "C:\\root");

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.MissingTerm, Is.Null);
            Assert.That(ToolCallContext.Current.TargetFilePath, Is.Null);
            Assert.That(ToolCallContext.Current.SolutionRoot, Is.Null);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Enter_SolutionRootThrows_KeepsIdentity()
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                [AgentIdentityCapture.AgentIdKey] = JsonDocument.Parse("\"a1\"").RootElement.Clone(),
                ["filePath"] = JsonDocument.Parse("\"Foo.cs\"").RootElement.Clone(),
                ["methodName"] = JsonDocument.Parse("\"Bar\"").RootElement.Clone(),
            };

            Assert.DoesNotThrow(() => AgentIdentityCapture.Enter("GetMethodSource", arguments, () => 0, () => throw new InvalidOperationException("boom")));

            Assert.That(ToolCallContext.Current, Is.Not.Null);
            Assert.That(ToolCallContext.Current!.AgentId, Is.EqualTo("a1"));
            Assert.That(ToolCallContext.Current.MissingTerm, Is.EqualTo("Bar"));
            Assert.That(ToolCallContext.Current.SolutionRoot, Is.Null);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    private static Dictionary<string, object?> Args(string? agentId = null, string? agentType = null)
    {
        var args = new Dictionary<string, object?> { ["reason"] = "test message" };
        if (agentId is not null)
        {
            args[AgentIdentityCapture.AgentIdKey] = agentId;
        }

        if (agentType is not null)
        {
            args[AgentIdentityCapture.AgentTypeKey] = agentType;
        }

        return args;
    }

    private async Task<CallToolResult> CallProbeAsync(Dictionary<string, object?> args) =>
        await _client.CallToolAsync("AgentIdentityProbe", args!, cancellationToken: TestContext.CurrentContext.CancellationToken);
}

/// <summary>Test-only tool that captures the ambient tool call context.</summary>
[McpServerToolType]
public class AgentIdentityProbeTool
{
    public static ToolCallContext? LastSeen;

    [McpServerTool(Name = "AgentIdentityProbe")]
    [System.ComponentModel.Description("Test-only tool that captures the ambient tool call context.")]
    public string AgentIdentityProbe([System.ComponentModel.Description("Why you call this.")] string reason)
    {
        LastSeen = ToolCallContext.Current;
        return "probe ran";
    }
}
