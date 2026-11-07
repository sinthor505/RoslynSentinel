// DTO-EXEMPT: test fixture class, not a DTO.
using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the stale-workspace call-tool filter (<c>AddWorkspaceStaleStampFilter</c>): while a workspace reload
/// failure is latched it stamps <c>workspaceStale</c>, <c>workspaceStaleReason</c> and <c>workspaceStaleRecovery</c> onto
/// the response, and stamps nothing otherwise. Modelled on <see cref="HaltStampFilterTests"/> (real in-process
/// <see cref="McpClient"/>/<see cref="McpServer"/> pair); the failure is latched through the internal
/// <c>RecordReloadFailure</c> seam (InternalsVisibleTo from Common). See
/// docs/current/proposals/proposal_workspace_reload_failure_visibility.md (slice 3).
/// </summary>
[TestFixture]
[NonParallelizable]
public class WorkspaceStaleStampFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    // Workspace carries GetWorkspaceHealthTool.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

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
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise the filter without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    private async Task<(CallToolResult Result, string Text)> CallHealthAsync()
    {
        var result = await _client.CallToolAsync(
            "GetWorkspaceHealth",
            new Dictionary<string, object?> { ["reason"] = "test message" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (result, text);
    }

    [Test]
    public async Task NoLatchedFailure_ResponseCarriesNoStaleProperties()
    {
        var (result, text) = await CallHealthAsync();

        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");
        Assert.That(text, Does.Not.Contain("workspaceStale"));
    }

    [Test]
    public async Task LatchedReloadFailure_Response_IsStampedWithReasonAndRecovery()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        var failure = new InvalidOperationException("project file is not well-formed");
        manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Broken.csproj", failure, oldWorkspaceKept: true);
        var latched = manager.ReloadFailure;
        Assert.That(latched, Is.Not.Null, "precondition: the failure must be latched");

        var (result, text) = await CallHealthAsync();

        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body["workspaceStale"]?.GetValue<bool>(), Is.True, $"workspaceStale missing in: {text}");
        Assert.That(body["workspaceStaleReason"]?.GetValue<string>(), Is.EqualTo(WorkspaceStaleText.Reason(latched!)));
        Assert.That(body["workspaceStaleRecovery"]?.GetValue<string>(), Is.EqualTo(WorkspaceStaleText.Recovery));
    }

    [Test]
    public async Task FailureClearedBeforeTheCall_Response_IsNotStamped()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Broken.csproj", new InvalidOperationException("boom"), oldWorkspaceKept: true);
        manager.ClearReloadFailure();

        var (result, text) = await CallHealthAsync();

        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");
        Assert.That(text, Does.Not.Contain("workspaceStale"));
    }
}
