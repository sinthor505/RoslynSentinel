using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Pins where the tool-telemetry call-tool filter sits in the pipeline (third, right inside the echo filter, so it wraps
/// argument validation, ToolError and the breakers), plus a smoke test of the <see cref="ToolTelemetryRecorder"/> seam.
/// The full behaviour tests (outcomes, error codes, concurrency, status field) belong in this file too.
/// </summary>
[TestFixture]
public class ToolTelemetryFilterOrderTests
{
    [Test]
    public void CallToolFilters_AreRegisteredInTheDeclaredOrder_WithIdentityFirstAndTelemetryThird()
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
        Assert.That(names[0], Is.EqualTo("AgentIdentity"), "The identity filter must stay outermost so its keys never reach the echo or the argument validator.");
        Assert.That(names[1], Is.EqualTo("ToolCallEcho"), "The echo filter must sit outside every filter that can change the response.");
        Assert.That(names[2], Is.EqualTo("ToolTelemetry"), "The telemetry filter must be third so it wraps ToolError, argument validation and the breakers.");
        Assert.That(names, Does.Contain("ToolError"));
        Assert.That(host.Services.GetService<ToolTelemetry>(), Is.Not.Null, "AddRoslynSentinelToolsBasic must register the ToolTelemetry singleton.");
    }

    /// <summary>Builds the host the way the order test does; returns the filter names and the number of registered call-tool filters.</summary>
    private static (List<string> Names, int RegisteredCount) BuildCallToolFilters()
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
        return (RoslynSentinelServiceExtensionsBasic.CallToolFilterNames.ToList(), registered.Count);
    }

    // ErrorPolicyOptions is static process state and the filter list is read at build time, so these tests must not run in parallel.
    [Test]
    [NonParallelizable]
    public void CallToolFilters_InOffMode_HaveNoErrorPolicyFilter()
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        try
        {
            var (names, registeredCount) = BuildCallToolFilters();

            Assert.That(names, Does.Not.Contain("ErrorPolicy"), "Off mode must register no error-policy filter, so Off is byte-identical to today.");
            Assert.That(registeredCount, Is.EqualTo(names.Count), "Every name in CallToolFilterNames must be one registered call-tool filter, and vice versa.");
            Assert.That(names.Count, Is.EqualTo(14), "The call-tool filter count before the error-policy slice was 14.");
        }
        finally
        {
            ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        }
    }

    // ErrorPolicyOptions is static process state and the filter list is read at build time, so these tests must not run in parallel.
    [Test]
    [NonParallelizable]
    public void CallToolFilters_InPointerMode_PutErrorPolicyDirectlyOutsideWorkspaceNotLoaded()
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=pointer" });
        try
        {
            var (names, registeredCount) = BuildCallToolFilters();

            var workspaceNotLoaded = names.IndexOf("WorkspaceNotLoaded");
            Assert.That(workspaceNotLoaded, Is.GreaterThan(0), "WorkspaceNotLoaded must be a registered call-tool filter.");
            Assert.That(names[workspaceNotLoaded - 1], Is.EqualTo("ErrorPolicy"), "The error-policy filter must sit directly outside WorkspaceNotLoaded so it sees thrown and returned errors alike.");
            Assert.That(names.Count, Is.EqualTo(15), "Pointer mode adds exactly one call-tool filter to the 14 of Off mode.");
            Assert.That(registeredCount, Is.EqualTo(names.Count), "Every name in CallToolFilterNames must be one registered call-tool filter, and vice versa.");
        }
        finally
        {
            ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        }
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
