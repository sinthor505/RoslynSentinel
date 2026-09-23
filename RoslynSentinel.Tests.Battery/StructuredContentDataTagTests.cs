// Structured-content + DataTag-chaining POC coverage for proposal_structuredcontent_rollout.md
// and proposal_datatag_chaining_contract.md. Exercises LocateSymbol (SentinelSymbolTools) and
// ModifyModifier (SentinelRefactoringTools) end to end through the real MCP dispatch pipeline
// (McpClient over an in-process pipe transport), following the pattern established by
// McpServerStatusStructuredContentTests.cs and LargeResultOffloadFilterTests.cs. Both tools need
// a real loaded solution (unlike McpServerStatus), so this uses TestSolutionFixture the same way
// LargeResultOffloadFilterTests.cs does.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class StructuredContentDataTagTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    // LocateSymbol lives in SentinelSymbolTools (Workspace mode); ModifyModifier lives in
    // SentinelRefactoringTools (Refactor mode) - see ToolClassRegistry.cs. Both modes are needed.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor" };

    private const string ProbeSource = @"namespace ContosoOrders.Core;

public class DataTagProbe
{
    public void Target() { }
}
";

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "DataTagProbe.cs"),
                ProbeSource,
                reloadSolution: false);
        }

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

        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise LocateSymbol/ModifyModifier without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    // --- LocateSymbol ---

    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task LocateSymbol_ListTools_AdvertisesRealOutputSchema_WithProducesTagVendorKeys()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var locateSymbolTool = tools.Single(t => t.Name == "LocateSymbol");

        Assert.That(locateSymbolTool.ProtocolTool.OutputSchema, Is.Not.Null,
            "LocateSymbol should advertise a real outputSchema once OutputSchemaType is set on the attribute.");

        var schema = locateSymbolTool.ProtocolTool.OutputSchema!.Value;
        Assert.That(schema.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "The advertised outputSchema should be a real JSON Schema object, not the bare `true` schema.");

        // Data is IReadOnlyList<LocatedSymbolInfo> -> outputSchema.properties.data.items should carry
        // x-produces-tag on the DocCommentId-tagged property, proving the McpToolSchemaPatcher rewrite
        // reaches into array-item schemas, not just top-level object properties.
        var dataItemsProps = schema
            .GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("properties");

        var docCommentIdNode = dataItemsProps.GetProperty("docCommentId");
        Assert.That(docCommentIdNode.TryGetProperty("x-produces-tag", out var tagValue), Is.True,
            "LocatedSymbolInfo.DocCommentId is tagged [Produces(DataTag.DocCommentId)] - expected x-produces-tag on its schema node.");
        Assert.That(tagValue.GetString(), Is.EqualTo("DocCommentId"));
    }

    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task LocateSymbol_Call_PopulatesStructuredContent_MatchingActualData()
    {
        var result = await _client.CallToolAsync(
            "LocateSymbol",
            new Dictionary<string, object?> { ["reason"] = "test message", ["symbolName"] = "Target" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.StructuredContent, Is.Not.Null,
            "CallToolResult.StructuredContent should be populated now that UseStructuredContent is true.");

        var structured = result.StructuredContent!.Value;
        Assert.That(structured.ValueKind, Is.EqualTo(JsonValueKind.Object));

        var dataArray = structured.GetProperty("successData");
        Assert.That(dataArray.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(dataArray.GetArrayLength(), Is.GreaterThan(0));

        var first = dataArray[0];
        Assert.That(first.GetProperty("symbolName").GetString(), Is.EqualTo("Target"));
        Assert.That(first.GetProperty("docCommentId").GetString(), Does.Contain("Target"));

        // Cross-check against the legacy TextContentBlock so this proves both channels describe the
        // same underlying call, matching the McpServerStatus spike's cross-check discipline.
        var textBlock = result.Content.OfType<TextContentBlock>().Single();
        using var textDoc = JsonDocument.Parse(textBlock.Text);
        Assert.That(textDoc.RootElement.GetProperty("successData")[0].GetProperty("symbolName").GetString(),
            Is.EqualTo(first.GetProperty("symbolName").GetString()));
    }

    // --- ModifyModifier ---

    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task ModifyModifier_ListTools_AdvertisesRealOutputSchema()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var modifyModifierTool = tools.Single(t => t.Name == "ModifyModifier");

        Assert.That(modifyModifierTool.ProtocolTool.OutputSchema, Is.Not.Null,
            "ModifyModifier should advertise a real outputSchema once OutputSchemaType is set on the attribute.");

        var schema = modifyModifierTool.ProtocolTool.OutputSchema!.Value;
        Assert.That(schema.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "The advertised outputSchema should be a real JSON Schema object, not the bare `true` schema.");
    }

    [Explicit("StructuredContent is currently not implemented")]
    [Test]
    public async Task ModifyModifier_Call_PopulatesStructuredContent_MatchingActualData()
    {
        var result = await _client.CallToolAsync(
            "ModifyModifier",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["filepath"] = Path.Combine("ContosoOrders.Core", "DataTagProbe.cs"),
                ["targetName"] = "DataTagProbe",
                ["modifier"] = "sealed",
                ["action"] = "add",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.StructuredContent, Is.Not.Null,
            "CallToolResult.StructuredContent should be populated now that UseStructuredContent is true.");

        var structured = result.StructuredContent!.Value;
        Assert.That(structured.ValueKind, Is.EqualTo(JsonValueKind.Object));

        var data = structured.GetProperty("successData");
        Assert.That(data.GetProperty("changeId").GetString(), Is.Not.Null.And.Not.Empty,
            "AppliedChangeSummary.ChangeId should be populated on the applied success path.");
        Assert.That(data.GetProperty("status").GetString(), Is.EqualTo("applied"));

        var textBlock = result.Content.OfType<TextContentBlock>().Single();
        using var textDoc = JsonDocument.Parse(textBlock.Text);
        Assert.That(textDoc.RootElement.GetProperty("successData").GetProperty("changeId").GetString(),
            Is.EqualTo(data.GetProperty("changeId").GetString()));
    }
}
