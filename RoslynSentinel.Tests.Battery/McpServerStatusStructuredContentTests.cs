using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class McpServerStatusStructuredContentTests
{
    // Added by AddMember (expected - used for diagnostics)
    private IHost _host = null!;
    // Added by AddMember (expected - used for diagnostics)
    private McpClient _client = null!;
    // Added by AddMember (expected - used for diagnostics)
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };
    // Added by AddMember (expected - used for diagnostics)
    [SetUp]
    public async Task SetUp()
    {
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

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task McpServerStatus_ListTools_AdvertisesOutputSchema()
    {
        Assert.Ignore("StructuredContent is currently not implemented");

        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var statusTool = tools.Single(t => t.Name == "McpServerStatus");

        Assert.That(statusTool.ProtocolTool.OutputSchema, Is.Not.Null,
            "McpServerStatus should advertise a real outputSchema once OutputSchemaType is set on the attribute.");

        var schemaKind = statusTool.ProtocolTool.OutputSchema!.Value.ValueKind;
        Assert.That(schemaKind, Is.EqualTo(JsonValueKind.Object),
            "The advertised outputSchema should be a real JSON Schema object, not the bare `true` " +
            "schema STJ emits for typeof(object) -- that shape is known to break LM Studio's tools/list handling.");
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task McpServerStatus_Call_PopulatesStructuredContent_MatchingTextContent()
    {
        Assert.Ignore("StructuredContent is currently not implemented");

        var result = await _client.CallToolAsync(
            "McpServerStatus",
            new Dictionary<string, object?> { ["reason"] = "test message" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.StructuredContent, Is.Not.Null,
            "CallToolResult.StructuredContent should be populated now that UseStructuredContent is true.");

        var structured = result.StructuredContent!.Value;
        Assert.That(structured.ValueKind, Is.EqualTo(JsonValueKind.Object));

        var deserialized = JsonSerializer.Deserialize<McpServerStatusResult>(
            structured.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.ServerPid, Is.GreaterThan(0), "ServerPid should be a valid process ID.");
        Assert.That(deserialized.SessionHalted, Is.False, "A fresh test host should not start in a halted session state.");
        Assert.That(deserialized.Breakers, Is.Not.Null);
        Assert.That(deserialized.ToolSurface, Is.Not.Null);
        Assert.That(deserialized.ToolSurface.ActiveToolClasses, Is.Not.Empty,
            "SentinelServerStatusTools itself should be among the active tool classes.");

        // Cross-check structured content against the legacy TextContentBlock so the spike proves both
        // channels describe the same underlying call, not just that StructuredContent exists.
        // McpServerStatus is tagged [Produces(DataTag.ResultOnly)], so its text payload is the bare
        // result object at the root -- unlike the "data"-wrapped envelope this same tool call's own
        // MCP response uses at the harness/dispatch layer -- so read sessionHalted directly.
        var textBlock = result.Content.OfType<TextContentBlock>().Single();
        using var textDoc = JsonDocument.Parse(textBlock.Text);
        Assert.That(textDoc.RootElement.TryGetProperty("sessionHalted", out var sessionHaltedProp), Is.True,
            "Expected the ResultOnly text payload to carry sessionHalted at the root, with no wrapping envelope.");
        Assert.That(sessionHaltedProp.GetBoolean(), Is.EqualTo(deserialized.SessionHalted));
    }
}
