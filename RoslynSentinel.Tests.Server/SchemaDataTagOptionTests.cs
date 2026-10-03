// Coverage for proposal_reduce_tool_schema_token_cost.md Step 1: x-consumes-tag / x-produces-tag
// and "default": null are omitted from tools/list input schemas unless --emit-datatags is set.
// Runs through the real MCP dispatch pipeline (McpClient over an in-process pipe transport), the
// same host shape as McpServerStatusStructuredContentTests.cs. No solution load is needed: only
// tools/list is exercised.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class SchemaDataTagOptionTests
{
    // ReplaceSnippet/ModifyModifier live in Refactor mode, ReadFile/LocateSymbol in Workspace mode.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor" };

    [TearDown]
    public void TearDown()
    {
        // Static options: reset so other fixtures see the default (off).
        SchemaOptions.Configure([]);
    }

    private static async Task<List<(string Name, JsonElement InputSchema)>> ListInputSchemasAsync(string[] startupArgs)
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
        _ = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Clone so the elements outlive the client/host disposal.
        var result = tools.Select(t => (t.Name, t.ProtocolTool.InputSchema.Clone())).ToList();
        await host.StopAsync();
        return result;
    }

    private static bool HasNullDefault(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "default" && property.Value.ValueKind == JsonValueKind.Null)
                    {
                        return true;
                    }

                    if (HasNullDefault(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(HasNullDefault);
            default:
                return false;
        }
    }

    [Test]
    public async Task ListTools_ByDefault_EmitsNoDataTagKeys()
    {
        var schemas = await ListInputSchemasAsync([]);

        Assert.That(schemas, Is.Not.Empty);
        foreach (var (name, schema) in schemas)
        {
            var raw = schema.GetRawText();
            Assert.That(raw, Does.Not.Contain("x-consumes-tag"), $"{name} should not carry x-consumes-tag by default.");
            Assert.That(raw, Does.Not.Contain("x-produces-tag"), $"{name} should not carry x-produces-tag by default.");
        }
    }

    [Test]
    public async Task ListTools_ByDefault_EmitsNoNullDefaults()
    {
        var schemas = await ListInputSchemasAsync([]);

        Assert.That(schemas, Is.Not.Empty);
        var offenders = schemas.Where(s => HasNullDefault(s.InputSchema)).Select(s => s.Name).ToList();
        Assert.That(offenders, Is.Empty, "No tool input schema should carry \"default\": null when --emit-datatags is off.");
    }

    [Test]
    public async Task ListTools_WithEmitDataTagsFlag_EmitsConsumesTags()
    {
        var schemas = await ListInputSchemasAsync(["--emit-datatags"]);

        Assert.That(SchemaOptions.EmitDataTags, Is.True);
        Assert.That(schemas.Any(s => s.InputSchema.GetRawText().Contains("x-consumes-tag")), Is.True,
            "With --emit-datatags, at least one tool parameter (e.g. filePath) should carry x-consumes-tag.");

        // Guards the "no null defaults" default-off test against being vacuous: with the flag on,
        // optional parameters do still carry "default": null.
        Assert.That(schemas.Any(s => HasNullDefault(s.InputSchema)), Is.True,
            "With --emit-datatags, optional parameters keep their \"default\": null entries.");
    }

    [Test]
    public void Configure_Absent_LeavesItOff()
    {
        SchemaOptions.Configure([]);
        Assert.That(SchemaOptions.EmitDataTags, Is.False);
    }

    [TestCase("--emit-datatags")]
    [TestCase("--emit-datatags=true")]
    [TestCase("--emit-datatags=1")]
    public void Configure_FlagForms_TurnItOn(string arg)
    {
        SchemaOptions.Configure([arg]);
        Assert.That(SchemaOptions.EmitDataTags, Is.True);
    }

    [TestCase("--emit-datatags=false")]
    [TestCase("--emit-datatags=0")]
    public void Configure_FalseOrZero_LeavesItOff(string arg)
    {
        SchemaOptions.Configure([arg]);
        Assert.That(SchemaOptions.EmitDataTags, Is.False);
    }

    [Test]
    public void Configure_BareFlagFollowedByAnotherFlag_DoesNotConsumeItAsValue()
    {
        SchemaOptions.Configure(["--emit-datatags", "--echo-tool-args=false"]);
        Assert.That(SchemaOptions.EmitDataTags, Is.True);
    }
}
