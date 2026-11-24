// Coverage for plan_tool_surface_controller_stage_e.md Step 10: ToolArgumentValidator caches each tool's schema per
// ToolSurfaceSnapshot instance, and a Validate call reads only the snapshot it is given (or the server's current one).
// Covers two hosts in one process with no reset between them, a refresh that changes the schema, a toolset
// re-enable, and a captured snapshot that overrides the server's current one. Same in-process host shape as
// SchemaHiddenParametersTests.cs; the claude-lean host follows ToolSurfaceControllerTests.BuildHarness.
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

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
[NonParallelizable] // mutates the process-wide SchemaOptions (profile and hidden list) that every host build reads
public class ToolSurfaceCacheTests
{
    private const string ClaudeLeanMode = "claude-lean";
    private const string HideValidateOnApply = "--hide-params=validateOnApply";

    // WholeFileWrite registers WholeFileWriteTools (ApplyDiff). Outside claude-lean it is a plain static class, not a toolset.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor", "WholeFileWrite" };

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // Process-static: the profile and the explicit hidden list. Hosts built after this read them at build time.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
    }

    // Runs one in-process host and client for the action, then stops the host. The host is exposed so the action can
    // reach the live tool collection and the controller. The start-up args are applied when the host is built.
    private static async Task<T> RunHostAsync<T>(string[] startupArgs, Func<IHost, McpClient, Task<T>> action)
    {
        SchemaOptions.Configure(startupArgs);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        var hostRun = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var result = await action(host, client);
        await host.StopAsync();
        await hostRun;
        return result;
    }

    private static string CallText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // Calls ReplaceSnippet with action validate (no solution is loaded, so the tool may report its own error; the
    // assertions only look at whether the validator refused or rejected a parameter).
    private static async Task<string> CallReplaceSnippetAsync(McpClient client, Dictionary<string, object?> extra)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["reason"] = "tool surface cache test",
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
        return CallText(result);
    }

    // Builds a claude-lean host (not started) with the controller and toolset service registered, as BuildHarness does.
    private static IHost BuildClaudeLeanHost()
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ClaudeLeanMode };
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, include);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        return hostBuilder.Build();
    }

    // A standalone McpServer over the host's tool collection, never started. Validate reads the tool schemas through
    // server.ServerOptions and the controller through server.Services; the pipes only satisfy the transport.
    private static McpServer CreateServerOver(IHost host)
    {
        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var input = new Pipe();
        var output = new Pipe();
        var transport = new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream());
        return McpServer.Create(transport, options, NullLoggerFactory.Instance, host.Services);
    }

    // Returns the schema with one top-level property removed, also from required, so the emitted shape stays consistent.
    private static JsonElement WithoutParameter(JsonElement schema, string name)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["properties"]!.AsObject().Remove(name);
        if (node["required"] is JsonArray required)
        {
            var kept = required.Where(item => item?.GetValue<string>() != name).Select(item => item!.DeepClone()).ToArray();
            node["required"] = new JsonArray(kept);
        }

        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    [Test]
    public async Task TwoHostsInOneProcess_ValidatorUsesEachHostsOwnSchema()
    {
        // Host A hides validateOnApply from the emitted schema, so the call is refused for that name.
        var hostAText = await RunHostAsync([HideValidateOnApply], (_, client) =>
            CallReplaceSnippetAsync(client, new() { ["validateOnApply"] = false }));
        Assert.That(hostAText, Does.Contain("validateOnApply:false is not available"),
            $"Host A hides validateOnApply, so the call must be refused. Got: {hostAText}");

        // Host B has no hide args and runs with no reset in between. Its own schema lists validateOnApply, so the
        // cached schema of host A must not be reused for it.
        var hostBText = await RunHostAsync([], (_, client) =>
            CallReplaceSnippetAsync(client, new() { ["validateOnApply"] = false }));
        Assert.That(hostBText, Does.Not.Contain("validateOnApply:false is not available"),
            $"Host B does not hide validateOnApply, so it must not be refused. Got: {hostBText}");
        Assert.That(hostBText, Does.Not.Contain("Unknown parameter"),
            $"Host B declares validateOnApply, so it must not be an unknown parameter. Got: {hostBText}");
    }

    [Test]
    public async Task SchemaChangeThenRefresh_ValidatorReadsTheNewSchema()
    {
        var outcome = await RunHostAsync([], async (host, client) =>
        {
            var controller = host.Services.GetRequiredService<ToolSurfaceController>();
            var built = controller.Refresh("test");

            var beforeText = await CallReplaceSnippetAsync(client, new() { ["returnDiff"] = true });
            Assert.That(beforeText, Does.Not.Contain("Unknown parameter"),
                $"returnDiff is declared, so it must be accepted before the schema change. Got: {beforeText}");

            // Remove returnDiff from the live tool's schema in place, then rebuild the snapshot.
            var tool = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
                .First(candidate => candidate.ProtocolTool.Name == "ReplaceSnippet");
            tool.ProtocolTool.InputSchema = WithoutParameter(tool.ProtocolTool.InputSchema, "returnDiff");
            var refreshed = controller.Refresh("test");
            Assert.That(refreshed.Version, Is.GreaterThan(built.Version));

            var afterText = await CallReplaceSnippetAsync(client, new() { ["returnDiff"] = true });
            return (beforeText, afterText);
        });

        Assert.That(outcome.afterText, Does.Contain("Unknown parameter"),
            $"After the refresh the removed parameter must be rejected as unknown. Got: {outcome.afterText}");
    }

    [Test]
    public async Task ToolsetReenable_NewSnapshotEachTime_ValidatorStillValidatesTheReaddedTool()
    {
        using var host = BuildClaudeLeanHost();
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        var toolsets = host.Services.GetRequiredService<ToolsetService>();

        var initial = controller.Refresh("test").Version;

        toolsets.SetEnabled(ToolSetName.git, true);
        var enabled = controller.Current.Version;
        toolsets.SetEnabled(ToolSetName.git, false);
        var disabled = controller.Current.Version;
        toolsets.SetEnabled(ToolSetName.git, true);
        var reenabled = controller.Current;

        Assert.That(enabled, Is.GreaterThan(initial), "Enabling git must bump the snapshot version.");
        Assert.That(disabled, Is.GreaterThan(enabled), "Disabling git must bump the snapshot version.");
        Assert.That(reenabled.Version, Is.GreaterThan(disabled), "Re-enabling git must bump the snapshot version again.");
        Assert.That(reenabled.Find("GitRemote"), Is.Not.Null, "GitRemote must be exposed after the second enable.");

        await using var server = CreateServerOver(host);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["reason"] = JsonSerializer.SerializeToElement("tool surface cache test"),
            ["operation"] = JsonSerializer.SerializeToElement("branch"),
            ["bogusParam"] = JsonSerializer.SerializeToElement("x"),
        };
        var error = ToolArgumentValidator.Validate(server, "GitRemote", arguments, surface: reenabled);
        Assert.That(error ?? string.Empty, Does.Contain("Unknown parameter"),
            $"bogusParam must be rejected by the re-added GitRemote schema. Got: {error}");
        Assert.That(error ?? string.Empty, Does.Contain("bogusParam"));
    }

    [Test]
    public async Task CapturedSnapshot_IsWhatThePassReads()
    {
        using var host = BuildClaudeLeanHost();
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        var current = controller.Refresh("test");

        // Same snapshot except ReplaceSnippet lists notAParameter as hidden.
        var fabricated = current with
        {
            Tools = current.Tools
                .Select(tool => string.Equals(tool.Name, "ReplaceSnippet", StringComparison.Ordinal)
                    ? tool with { HiddenParameters = ["notAParameter"] }
                    : tool)
                .ToList(),
        };

        await using var server = CreateServerOver(host);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["reason"] = JsonSerializer.SerializeToElement("tool surface cache test"),
            ["action"] = JsonSerializer.SerializeToElement("validate"),
            ["notAParameter"] = JsonSerializer.SerializeToElement("x"),
        };

        var accepted = ToolArgumentValidator.Validate(server, "ReplaceSnippet", arguments, surface: fabricated);
        Assert.That(accepted ?? string.Empty, Does.Not.Contain("Unknown parameter"),
            $"The captured snapshot hides notAParameter, so the pass must accept it. Got: {accepted}");

        var rejected = ToolArgumentValidator.Validate(server, "ReplaceSnippet", arguments);
        Assert.That(rejected ?? string.Empty, Does.Contain("Unknown parameter"),
            $"Without a captured snapshot the server's current snapshot does not hide it, so it must be rejected. Got: {rejected}");
        Assert.That(rejected ?? string.Empty, Does.Contain("notAParameter"));
    }
}
