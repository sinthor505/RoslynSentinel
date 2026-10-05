// TagTestCategories' `framework` parameter is the closed TestCategoryFramework enum. These tests run the real MCP
// dispatch pipeline (McpClient over an in-process pipe transport, same host shape as SchemaDataTagOptionTests) and check
// (1) the EMITTED input schema lists the enum as string names (never integers) with default "Auto", and
// (2) how a wrong-case value is treated by the argument-validation filter. No solution load is needed.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // SchemaOptions is process-wide static state
public class TestCategoryToolSchemaTests
{
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "TestCategories" };

    private static async Task<T> WithClientAsync<T>(Func<McpClient, Task<T>> body)
    {
        SchemaOptions.Configure([]);

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
        var result = await body(client);

        await host.StopAsync();
        await hostRun;
        return result;
    }

    [Test]
    [Description("The emitted schema lists framework as the string names Auto, NUnit, XUnit, MSTest (not integers), default Auto")]
    public async Task EmittedSchema_FrameworkIsStringEnumOfNames()
    {
        var framework = await WithClientAsync(async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            var tool = tools.Single(t => t.Name == "TagTestCategories");
            return tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("framework").Clone();
        });

        Assert.That(framework.GetProperty("type").ValueKind, Is.EqualTo(JsonValueKind.String).Or.EqualTo(JsonValueKind.Array));
        var names = framework.GetProperty("enum").EnumerateArray().ToList();
        Assert.That(names.All(n => n.ValueKind == JsonValueKind.String), Is.True, $"enum values must be strings: {framework.GetRawText()}");
        Assert.That(names.Select(n => n.GetString()), Is.EqualTo(new[] { "Auto", "NUnit", "XUnit", "MSTest" }));
        Assert.That(framework.GetRawText(), Does.Not.Contain("\"integer\""));
    }

    [Test]
    [Description("framework is optional in the schema (defaults to Auto), while targets stays required")]
    public async Task EmittedSchema_FrameworkIsNotRequired()
    {
        var required = await WithClientAsync(async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            var tool = tools.Single(t => t.Name == "TagTestCategories");
            return tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        });

        Assert.That(required, Does.Contain("targets"));
        Assert.That(required, Does.Not.Contain("framework"));
        Assert.That(required, Does.Not.Contain("applyProjects"));
    }

    [Test]
    [Description("applyProjects is emitted as an optional parameter so subset edits do not need testScope")]
    public async Task EmittedSchema_ApplyProjectsIsAnOptionalParameter()
    {
        var (names, required) = await WithClientAsync(async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
            var schema = tools.Single(t => t.Name == "TagTestCategories").ProtocolTool.InputSchema;
            return (
                schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList(),
                schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
        });

        Assert.That(names, Does.Contain("applyProjects"));
        Assert.That(names, Does.Contain("testScope"));
        Assert.That(required, Does.Not.Contain("applyProjects"));
    }

    private static async Task<(bool IsError, string Text)> CallWithFrameworkAsync(string framework)
    {
        return await WithClientAsync(async client =>
        {
            var result = await client.CallToolAsync(
                "TagTestCategories",
                new Dictionary<string, object?>
                {
                    ["reason"] = "checking how the framework enum value binds",
                    ["targets"] = "Targets",
                    ["framework"] = framework,
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            return (result.IsError == true, text);
        });
    }

    [Test]
    [Description("An exact enum name reaches the tool body (no binding or validation error about framework)")]
    public async Task Call_WithExactEnumName_PassesValidation()
    {
        var (_, text) = await CallWithFrameworkAsync("NUnit");

        Assert.That(text, Does.Not.Contain("not a valid value for parameter 'framework'"));
    }

    [Test]
    [Description("A wrong-case name is not silently repaired: the validation filter rejects it, names the closest valid value and lists them all")]
    public async Task Call_WithWrongCaseName_IsRejectedWithClosestSuggestion()
    {
        var (isError, text) = await CallWithFrameworkAsync("nunit");

        Assert.That(isError, Is.True, text);
        Assert.That(text, Does.Contain("'nunit' is not a valid value for parameter 'framework'"));
        Assert.That(text, Does.Contain("Did you mean 'NUnit'?"));
        Assert.That(text, Does.Contain("Auto, NUnit, XUnit, MSTest"));
    }

    [Test]
    [Description("A name outside the enum is rejected before the tool runs, listing the valid values")]
    public async Task Call_WithUnknownName_IsRejectedListingValidValues()
    {
        var (isError, text) = await CallWithFrameworkAsync("junit");

        Assert.That(isError, Is.True, text);
        Assert.That(text, Does.Contain("not a valid value for parameter 'framework'"));
        Assert.That(text, Does.Contain("Auto, NUnit, XUnit, MSTest"));
    }
}
