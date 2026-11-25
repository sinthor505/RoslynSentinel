// Plan: plan_startup_configuration_registry.md, test T7. Drives BreakerFilters.AddConfigurationHaltFilter with an
// injected ConfigurationReport through a real in-process MCP client/server pair (paired Pipes plus
// WithStreamServerTransport, as in ErrorPolicyFilterTests.RunPipelineAsync), with two probe tools and no other tool
// classes: the filter is registered on its own, so nothing else can answer first.

using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ConfigurationHaltFilterTests
{
    private const string ProbeToolName = "ConfigHaltProbe";
    private const string ProbeReply = "probe reached the tool";

    private static readonly ConfigurationProblem[] TwoProblems =
    [
        new(ConfigurationProblemKind.InvalidValue, "Fixture.Count", "--test-fixture-count", "TESTFIXTURE_COUNT", "abc", "integer"),
        new(ConfigurationProblemKind.UnknownFlag, null, "--test-fixture-nope", null, null, null),
    ];

    private static ConfigurationReport ReportWith(bool halted) =>
        new(Array.Empty<StartupSetting>(), halted ? TwoProblems : Array.Empty<ConfigurationProblem>(), Array.Empty<string>(), halted);

    [Test]
    public async Task Halted_RefusesNonAllowed()
    {
        var report = ReportWith(halted: true);

        var result = await CallAsync(report, ProbeToolName);

        Assert.That(result.IsError, Is.True, "a halted configuration refuses every tool except McpServerStatus");
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Does.Not.Contain(ProbeReply), "the refusal must come from the filter, not the tool");

        using var doc = JsonDocument.Parse(text);
        var error = doc.RootElement.GetProperty("errorData");
        Assert.That(error.GetProperty("errorCode").GetString(), Is.EqualTo(ToolErrorCode.ConfigurationInvalid));
        Assert.That(error.GetProperty("structuredDetail").GetArrayLength(), Is.EqualTo(report.Problems.Count),
            "StructuredDetail carries one entry per problem");
        Assert.That(error.GetProperty("message").GetString(), Does.Contain(report.Problems.Count.ToString()));

        // ResultError.Category is [JsonIgnore], so the mapped status is pinned on the mapping itself.
        Assert.That(
            ResultStatusExtensions.FromErrorCode(ToolErrorCode.ConfigurationInvalid),
            Is.EqualTo(ResultStatusExtensions.FromErrorCode(ToolErrorCode.PreconditionNotMet)));
    }

    [Test]
    public async Task Halted_AllowsMcpServerStatus()
    {
        var result = await CallAsync(ReportWith(halted: true), ServerStatusTools.ToolName);

        Assert.That(result.IsError, Is.Not.True, "McpServerStatus is the one tool a halted server still answers");
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Is.EqualTo(ProbeReply));
    }

    [Test]
    public async Task NotHalted_PassesThrough()
    {
        var result = await CallAsync(ReportWith(halted: false), ProbeToolName);

        Assert.That(result.IsError, Is.Not.True);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Is.EqualTo(ProbeReply));
    }

    /// <summary>
    /// Starts an in-process server whose only filter is the configuration halt filter over <paramref name="report"/>,
    /// with a probe tool under both the probe name and the McpServerStatus name, calls <paramref name="toolName"/>, and stops.
    /// </summary>
    private static async Task<CallToolResult> CallAsync(ConfigurationReport report, string toolName)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTools(new[]
        {
            McpServerTool.Create(new Func<string>(() => ProbeReply), new McpServerToolCreateOptions { Name = ProbeToolName, Description = "Test-only probe." }),
            McpServerTool.Create(new Func<string>(() => ProbeReply), new McpServerToolCreateOptions { Name = ServerStatusTools.ToolName, Description = "Test-only stand-in for the status tool." }),
        });
        mcpBuilder.WithRequestFilters(filters => BreakerFilters.AddConfigurationHaltFilter(filters, () => report));

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

        try
        {
            return await client.CallToolAsync(toolName, new Dictionary<string, object?>(), cancellationToken: TestContext.CurrentContext.CancellationToken);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
