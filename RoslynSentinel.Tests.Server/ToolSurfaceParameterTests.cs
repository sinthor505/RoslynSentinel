// Coverage for plan_tool_surface_controller_stage_d.md Step 9: the per-host ParameterSurface. Snapshot rows, hidden-parameter
// groups and unmatched hide rules come from the surface registered in DI; two hosts in one process keep their own rows; a
// harness-registered surface wins over the startup default; a stale hidden-name registry entry does not hide or accept
// anything once a controller is registered; and a toolset tool added at runtime is stripped by the same surface. Same
// in-process host shape as ToolSurfaceControllerTests (claude-lean, controller over the host), plus a stream client for
// the call-time checks. Tools are patched at creation, so SchemaOptions must be set before a host is built.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions (HiddenParameters), HiddenSchemaParams and the validator schema cache
[Category("ToolClassRegistry")]
public class ToolSurfaceParameterTests
{
    private const string ClaudeLeanMode = "claude-lean";
    private const string LeanValidateOnApply = "validateOnApply";

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // Process-static: the profile and explicit hidden list, the hidden-name registry, and the validator's schema cache.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(IHost host, Pipe clientToServer, Pipe serverToClient)
        {
            Host = host;
            ClientToServer = clientToServer;
            ServerToClient = serverToClient;
        }

        public IHost Host { get; }

        public Pipe ClientToServer { get; }

        public Pipe ServerToClient { get; }

        public void Dispose() => Host.Dispose();
    }

    // Builds a claude-lean host (not started). A harness surface, when given, is registered before the tool registration
    // so its TryAdd keeps it; without one the tool registration builds the startup default on first resolve.
    private static Harness BuildHarness(ParameterSurface? harnessSurface = null)
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();
        if (harnessSurface is not null)
        {
            services.AddSingleton(harnessSurface);
        }

        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ClaudeLeanMode };
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, include);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        return new Harness(hostBuilder.Build(), clientToServer, serverToClient);
    }

    private static async Task<T> WithClientAsync<T>(Harness harness, Func<McpClient, Task<T>> action)
    {
        var hostRun = harness.Host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: harness.ClientToServer.Writer.AsStream(),
            serverOutput: harness.ServerToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var result = await action(client);
        await harness.Host.StopAsync();
        await hostRun;
        return result;
    }

    private static Task<List<(string Name, JsonElement InputSchema)>> ListInputSchemasAsync(Harness harness) =>
        WithClientAsync(harness, async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

            // Clone so the elements outlive the client/host disposal.
            return tools.Select(t => (t.Name, t.ProtocolTool.InputSchema.Clone())).ToList();
        });

    private static JsonElement SchemaFor(List<(string Name, JsonElement InputSchema)> schemas, string toolName)
    {
        var found = schemas.Where(s => s.Name == toolName).Select(s => s.InputSchema).ToList();
        Assert.That(found, Has.Count.EqualTo(1), $"{toolName} should be listed exactly once in claude-lean.");
        return found[0];
    }

    private static HashSet<string> TopLevelProperties(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    // Calls ReplaceSnippet with action validate (no solution is loaded, so the tool may report its own error; the
    // assertions only look at whether the validator refused or rejected a parameter).
    private static Task<string> CallReplaceSnippetAsync(Harness harness, Dictionary<string, object?> extra) =>
        WithClientAsync(harness, async client =>
        {
            var arguments = new Dictionary<string, object?>
            {
                ["reason"] = "Verify the per-host parameter surface at the call boundary",
                ["action"] = "validate",
            };
            foreach (var pair in extra)
            {
                arguments[pair.Key] = pair.Value;
            }

            var result = await client.CallToolAsync(
                "ReplaceSnippet",
                arguments,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        });

    [Test]
    public void Snapshot_CarriesTheRegisteredParameterSurface()
    {
        using var harness = BuildHarness();

        var surface = harness.Host.Services.GetRequiredService<ParameterSurface>();
        var current = harness.Host.Services.GetRequiredService<ToolSurfaceController>().Current;

        Assert.That(current.Parameters, Is.SameAs(surface));
    }

    [Test]
    public void Snapshot_HiddenParameterGroups_NameTheWinningRule()
    {
        SchemaOptions.Configure(["--schema-profile=lean"]);
        using var harness = BuildHarness();

        var current = harness.Host.Services.GetRequiredService<ToolSurfaceController>().Current;

        var group = current.HiddenParameterGroups?.FirstOrDefault(g => g.Parameter == LeanValidateOnApply);
        Assert.That(group, Is.Not.Null, "The lean profile should produce a hidden group for validateOnApply.");
        Assert.That(group!.Source, Is.EqualTo(ParameterRuleSource.Profile));
        Assert.That(group.Origin, Is.EqualTo("--schema-profile=lean"));
        Assert.That(group.Tools, Does.Contain("ReplaceSnippet"));
    }

    [Test]
    public void Snapshot_UnmatchedHideRules_ListsMisspeltHideParam()
    {
        SchemaOptions.Configure(["--hide-params=validateOnAply"]);
        using var harness = BuildHarness();

        var current = harness.Host.Services.GetRequiredService<ToolSurfaceController>().Current;

        var unmatched = current.UnmatchedHideRules ?? [];
        Assert.That(unmatched.Select(rule => rule.Parameter), Does.Contain("validateOnAply"));
    }

    [Test]
    public void TwoHostsInOneProcess_SnapshotRowsArePerHost()
    {
        // Host A is fully resolved (tools built, snapshot read) before host B's options are configured: tool build is lazy.
        SchemaOptions.Configure(["--hide-params=validateOnApply"]);
        using var hostA = BuildHarness();
        var controllerA = hostA.Host.Services.GetRequiredService<ToolSurfaceController>();
        Assert.That(hostA.Host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection, Is.Not.Empty);
        var rowA = controllerA.Current.Find("ReplaceSnippet");
        Assert.That(rowA, Is.Not.Null);
        Assert.That(rowA!.HiddenParameters, Does.Contain("validateOnApply"));

        // There is no reset between the hosts: the per-host snapshot alone must decide host B's row.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        using var hostB = BuildHarness();
        var rowB = hostB.Host.Services.GetRequiredService<ToolSurfaceController>().Current.Find("ReplaceSnippet");

        Assert.That(rowB, Is.Not.Null);
        Assert.That(rowB!.HiddenParameters, Does.Not.Contain("validateOnApply"));
        Assert.That(rowA.HiddenParameters, Does.Contain("validateOnApply"));
    }

    [Test]
    public async Task HarnessRegisteredSurface_WinsOverStartupDefault()
    {
        var surface = new ParameterSurface(new ToolParameterSurfaceOptions(
        [
            new ParameterVisibilityRule(null, "returnDiff", ParameterVisibility.Hide, ParameterRuleSource.Harness, "test"),
        ]));
        using var harness = BuildHarness(surface);

        var current = harness.Host.Services.GetRequiredService<ToolSurfaceController>().Current;
        Assert.That(current.Parameters, Is.SameAs(surface));

        // One client session for both checks: WithClientAsync stops the host and closes the pipes, so a harness can
        // connect only once (a second WithClientAsync on the same harness times out in McpClient.CreateAsync).
        var (schema, text) = await WithClientAsync(harness, async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            var listed = tools.Single(t => t.Name == "ReplaceSnippet").ProtocolTool.InputSchema.Clone();
            var result = await client.CallToolAsync(
                "ReplaceSnippet",
                new Dictionary<string, object?>
                {
                    ["reason"] = "Verify the per-host parameter surface at the call boundary",
                    ["action"] = "validate",
                    ["returnDiff"] = true,
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            return (listed, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        });

        Assert.That(TopLevelProperties(schema), Does.Not.Contain("returnDiff"));
        Assert.That(text, Does.Not.Contain("Unknown parameter"), text);
        Assert.That(text, Does.Not.Contain("is not available for tool"), text);
    }

    [Test]
    public async Task ToolsetEnabledTool_IsStrippedByTheSameSurface()
    {
        var surface = new ParameterSurface(new ToolParameterSurfaceOptions(
        [
            new ParameterVisibilityRule("TypedMember", "typedType", ParameterVisibility.Hide, ParameterRuleSource.Harness, "test"),
        ]));
        using var harness = BuildHarness(surface);
        var controller = harness.Host.Services.GetRequiredService<ToolSurfaceController>();
        _ = controller.Current;
        Assert.That(controller.Current.IsExposed("TypedMember"), Is.False, "declarations is not enabled at start in claude-lean.");

        // The declarations set is disabled at start in claude-lean (ToolSurfaceControllerTests enables it the same way).
        harness.Host.Services.GetRequiredService<ToolsetService>().SetEnabled(ToolSetName.declarations, true);

        var row = controller.Current.Find("TypedMember");
        Assert.That(row, Is.Not.Null);
        Assert.That(row!.HiddenParameters, Does.Contain("typedType"));
        Assert.That(row.ListedParameters, Does.Not.Contain("typedType"));

        var schemas = await ListInputSchemasAsync(harness);
        Assert.That(TopLevelProperties(SchemaFor(schemas, "TypedMember")), Does.Not.Contain("typedType"));
    }

    // TODO WrapPath_HiddenKeyIsNotTreatedAsAFlatItemKey (ToolArgumentValidator.WrapFlatBatchParameters, IsHiddenParameter in
    // the flatKeys filter) is not covered: no probe found where a hidden top-level name is also a batchEdits item field of
    // the same tool. Lead: BatchTypes.cs LineBefore/LineAfter item fields vs lineBefore/lineAfter top-level on DeclarationTool.
}
