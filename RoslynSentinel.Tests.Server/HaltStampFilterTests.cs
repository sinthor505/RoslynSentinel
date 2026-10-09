using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the halt-stamping call-tool filter (<c>AddHaltStampFilter</c>): while any breaker is
/// tripped it stamps <c>isSessionHalted</c>, <c>sessionHaltKind</c>, <c>sessionHaltReason</c> and
/// <c>sessionHaltRecovery</c> onto the response - see
/// docs/current/plans/plan_external_file_drift_tool_and_halt_stamping.md (Decision 3, step 7). Runs against
/// a real in-process <see cref="McpClient"/>/<see cref="McpServer"/> pair (same pattern as
/// <see cref="ToolCallEchoFilterTests"/>) so the stamp is exercised in position, with the real breaker
/// filters inside it. The <c>externalDrift</c> kind is covered by <c>HaltInfoTests</c>: real drift
/// cannot be seeded cheaply here.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HaltStampFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;
    private IHost? _probeHost;
    private McpClient? _probeClient;

    // Workspace carries GetWorkspaceHealth/FindReferences; Admin carries ExternalFileDrift.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Admin" };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();
        (_host, _client) = await StartHostAsync(registerResetMutationBreakerProbe: false);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_probeClient is not null)
        {
            await _probeClient.DisposeAsync();
        }

        if (_probeHost is not null)
        {
            await _probeHost.StopAsync();
            _probeHost.Dispose();
        }

        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    private async Task<(IHost Host, McpClient Client)> StartHostAsync(bool registerResetMutationBreakerProbe)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        if (registerResetMutationBreakerProbe)
        {
            mcpBuilder.WithTools<ResetMutationBreakerProbeTool>();
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        var host = hostBuilder.Build();
        _ = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

        var loadResult = await client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise the filter without a loaded solution.");

        return (host, client);
    }

    private static async Task<(CallToolResult Result, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(
            tool,
            args!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (result, text);
    }

    private static Task<(CallToolResult Result, string Text)> CallHealthAsync(McpClient client) =>
        CallAsync(client, "GetWorkspaceHealth", new() { ["reason"] = "test message" });

    private static JsonObject ParseBody(string text) => JsonNode.Parse(text)!.AsObject();

    private static void AssertStampedAs(JsonObject body, string expectedKind)
    {
        Assert.That(body["isSessionHalted"]?.GetValue<bool>(), Is.True, $"isSessionHalted missing in: {body.ToJsonString()}");
        Assert.That(body["sessionHaltKind"]?.GetValue<string>(), Is.EqualTo(expectedKind));
        Assert.That(body["sessionHaltReason"]?.GetValue<string>(), Is.Not.Null.And.Not.Empty);
        Assert.That(body["sessionHaltRecovery"]?.GetValue<string>(), Is.Not.Null.And.Not.Empty);
    }

    private static void TripMutationBreaker(PersistentWorkspaceManager manager)
    {
        for (var i = 0; i < 8; i++)
        {
            manager.RecordBatchOutcome(0, 1, 0, 0);
        }

        Assert.That(((IManualCircuitBreaker)manager).IsTripped(), Is.True, "Eight zero-success batches should have tripped the mutation breaker.");
    }

    [Test]
    public async Task NothingTripped_ResponseCarriesNoHaltProperties()
    {
        var (health, healthText) = await CallHealthAsync(_client);
        Assert.That(health.IsError, Is.Not.True, $"Got: {healthText}");
        Assert.That(healthText, Does.Not.Contain("isSessionHalted"));
        Assert.That(healthText, Does.Not.Contain("sessionHaltKind"));

        var (_, driftText) = await CallAsync(_client, "ExternalFileDrift", new() { ["reason"] = "test message", ["operation"] = "Status" });
        Assert.That(driftText, Does.Not.Contain("isSessionHalted"));
    }

    [Test]
    public async Task UnrecoverableTripped_AllowedCall_IsStampedWithUnrecoverableKind()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        ((IUnrecoverableBreaker)manager).Trip("T", "id", "diag");

        var (result, text) = await CallHealthAsync(_client);
        Assert.That(result.IsError, Is.Not.True, $"GetWorkspaceHealth is allowed during an unrecoverable halt. Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "unrecoverable");
        Assert.That(
            body["sessionHaltReason"]!.GetValue<string>(),
            Is.EqualTo(((IUnrecoverableBreaker)manager).StateMessage()));

        // The four properties sit directly after the echo's toolCall, ahead of the original body.
        Assert.That(
            body.Select(p => p.Key).Take(5),
            Is.EqualTo(new[] { "toolCall", "isSessionHalted", "sessionHaltKind", "sessionHaltReason", "sessionHaltRecovery" }));
    }

    [Test]
    public async Task UnrecoverableTripped_RefusedCall_IsStampedWrapper_WithOriginalRefusalAsMessage()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        ((IUnrecoverableBreaker)manager).Trip("T", "id", "diag");
        var refusal = ((IUnrecoverableBreaker)manager).StateMessage();

        // FindReferences is not on the unrecoverable allow-list.
        var (result, text) = await CallAsync(_client, "FindReferences", new()
        {
            ["reason"] = "test message",
            ["symbolName"] = "EchoLone",
            ["kind"] = "callers",
        });
        Assert.That(result.IsError, Is.True, $"A non-allowed tool must be refused. Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "unrecoverable");
        Assert.That(body["message"]?.GetValue<string>(), Is.EqualTo(refusal));
    }

    [Test]
    public async Task UnrecoverableTripped_ExternalFileDriftCall_IsStamped_BecauseSkipAppliesOnlyToDriftKind()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        ((IUnrecoverableBreaker)manager).Trip("T", "id", "diag");

        var (result, text) = await CallAsync(_client, "ExternalFileDrift", new() { ["reason"] = "test message", ["operation"] = "Status" });
        Assert.That(result.IsError, Is.Not.True, $"ExternalFileDrift is allowed during an unrecoverable halt. Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "unrecoverable");
        Assert.That(body["message"]?.GetValue<string>(), Does.Contain("SessionHalted="));
    }

    [Test]
    public async Task MutationBreakerTripped_WithoutResetTool_RecoverySaysStopAndReport()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        TripMutationBreaker(manager);

        var (result, text) = await CallHealthAsync(_client);
        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "mutation");
        var recovery = body["sessionHaltRecovery"]!.GetValue<string>();
        Assert.That(recovery, Does.Contain("Stop and report"));
        Assert.That(recovery, Does.Not.Contain("ResetMutationBreaker"));
    }

    [Test]
    public async Task MutationBreakerTripped_WithResetToolRegistered_RecoveryNamesResetMutationBreaker()
    {
        (_probeHost, _probeClient) = await StartHostAsync(registerResetMutationBreakerProbe: true);
        var manager = _probeHost.Services.GetRequiredService<PersistentWorkspaceManager>();
        TripMutationBreaker(manager);

        var (result, text) = await CallHealthAsync(_probeClient);
        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "mutation");
        Assert.That(body["sessionHaltRecovery"]!.GetValue<string>(), Does.Contain("ResetMutationBreaker"));
    }

    [Test]
    public async Task OrientationBreakerTripped_RefusedCall_RecoveryIsTheBreakersStateMessage()
    {
        var manager = _host.Services.GetRequiredService<PersistentWorkspaceManager>();
        var tripped = false;
        for (var i = 0; i < 50 && !tripped; i++)
        {
            tripped = manager.RecordSearchOutcome(0);
        }

        Assert.That(tripped, Is.True, "Zero-match searches should have tripped the orientation breaker.");
        var stateMessage = ((IAutomaticCircuitBreaker)manager).StateMessage();
        Assert.That(stateMessage, Is.Not.Null.And.Not.Empty);

        // GetWorkspaceHealth is not on the orienting allow-list, so the orientation filter refuses it
        // (and, being a refusal, does not reset the breaker before the stamp reads its state).
        var (result, text) = await CallHealthAsync(_client);
        Assert.That(result.IsError, Is.True, $"Got: {text}");

        var body = ParseBody(text);
        AssertStampedAs(body, "orientation");
        Assert.That(body["sessionHaltRecovery"]!.GetValue<string>(), Is.EqualTo(stateMessage));
        Assert.That(body["message"]?.GetValue<string>(), Is.EqualTo(stateMessage));
    }
}

/// <summary>
/// Test-only stand-in for the Advanced <c>ResetMutationBreaker</c> tool, so a second host can prove the halt stamp
/// asks the live tool collection (not a static list) whether that recovery call exists.
/// </summary>
[McpServerToolType]
public class ResetMutationBreakerProbeTool
{
    [McpServerTool(Name = "ResetMutationBreaker")]
    [System.ComponentModel.Description("Test-only probe named like the real ResetMutationBreaker tool.")]
    public string ResetMutationBreaker() => "reset";
}
