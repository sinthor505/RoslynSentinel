using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Pins where the tool-telemetry call-tool filter sits in the pipeline (second, right inside the echo filter, so it wraps
/// argument validation, ToolError and the breakers), plus a smoke test of the <see cref="ToolTelemetryRecorder"/> seam.
/// The full behaviour tests (outcomes, error codes, concurrency, status field) belong in this file too.
/// </summary>
[TestFixture]
public class ToolTelemetryFilterOrderTests
{
    [Test]
    public void CallToolFilters_AreRegisteredInTheDeclaredOrder_WithTelemetrySecond()
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsBasic(services, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-lean" });

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        var registered = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters;
        var names = RoslynSentinelServiceExtensionsBasic.CallToolFilterNames;

        Assert.That(registered, Has.Count.EqualTo(names.Count), "Every name in CallToolFilterNames must be one registered call-tool filter, and vice versa.");
        Assert.That(names[0], Is.EqualTo("ToolCallEcho"), "The echo filter must stay outermost.");
        Assert.That(names[1], Is.EqualTo("ToolTelemetry"), "The telemetry filter must be second so it wraps ToolError, argument validation and the breakers.");
        Assert.That(names, Does.Contain("ToolError"));
        Assert.That(host.Services.GetService<ToolTelemetry>(), Is.Not.Null, "AddRoslynSentinelToolsBasic must register the ToolTelemetry singleton.");
    }

    [Test]
    public async Task RecordAsync_CountsSuccessAndErrorBodyAndException()
    {
        var telemetry = new ToolTelemetry(instanceId: "test", buildSha: null);

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => ValueTask.FromResult(new CallToolResult { IsError = false }));
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "{\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}" }],
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => throw new InvalidOperationException("boom")));

        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That(stats.ToolName, Is.EqualTo("Search"));
        Assert.That((stats.Calls, stats.Successes, stats.Errors), Is.EqualTo((3L, 1L, 2L)));
        Assert.That(stats.ErrorsByCode, Is.EquivalentTo(new Dictionary<string, int> { ["NotFound"] = 1, ["InvalidOperationException"] = 1 }));
    }
}
