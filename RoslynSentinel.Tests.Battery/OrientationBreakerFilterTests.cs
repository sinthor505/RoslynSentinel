// Real MCP client/server round-trip coverage for the orientation breaker request filter
// registered in ServiceRegistrationExtensionsBasic.AddRoslynSentinelToolsBasic (see
// docs/current/plan-orientation-breaker.md). Unlike OrientationBreakerTests.cs (which calls
// PersistentWorkspaceManager's breaker interfaces directly), this drives genuine JSON-RPC
// tool calls through the full filter chain, so it also proves the filter itself — not just
// the breaker state machine — behaves correctly: short-circuiting non-allowlisted tools while
// tripped, letting allowlisted tools through, and auto-resetting on success.
//
// Harness pattern (paired Pipes + WithStreamServerTransport + StreamClientTransport) copied
// from RoslynSentinel.Tests.Advanced\McpTasksHarnessTests.cs, the first fixture to prove this
// works at all — that one drives AddRoslynSentinelToolsAdvanced; this one drives
// AddRoslynSentinelToolsBasic, which is where the orientation breaker filter is registered.

using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class OrientationBreakerFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

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
            new Dictionary<string, object?> { ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load — cannot exercise the filter without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    private async Task<CallToolResult> SearchForGuaranteedNoMatchAsync(string pattern) =>
        await _client.CallToolAsync(
            "SearchSolutionText",
            new Dictionary<string, object?> { ["pattern"] = pattern, ["searchMode"] = "literal" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

    [Test]
    public async Task NormalSession_NeverTripped_AllToolsUnaffected()
    {
        var result = await _client.CallToolAsync(
            "ListAll",
            new Dictionary<string, object?>()!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
    }

    [Test]
    public async Task ThreeConsecutiveZeroMatchSearches_TripsBreaker_NonAllowlistedToolShortCircuited()
    {
        for (int i = 0; i < 3; i++)
        {
            var result = await SearchForGuaranteedNoMatchAsync($"ZzzNoSuchTokenAnywhereInTheSolution{i}Zzz");
            Assert.That(result.IsError, Is.Not.True, "SearchSolutionText itself should still succeed (zero matches is not a tool error).");
        }

        // ListWorkspaceSolutions is not on the allowlist (ListAll, ListSolutionItems, GetFileOutline, ReadFile).
        var blocked = await _client.CallToolAsync(
            "ListWorkspaceSolutions",
            new Dictionary<string, object?> { ["workspacePath"] = _fixture.SolutionDirectory }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(blocked.IsError, Is.True, "A non-allowlisted tool call should be short-circuited while the orientation breaker is tripped.");
        var text = string.Join(" ", blocked.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Does.Contain("Orientation breaker"));
    }

    [Test]
    public async Task TrippedBreaker_AllowlistedToolStillReachesRealTool_AndResetsBreaker()
    {
        for (int i = 0; i < 3; i++)
        {
            await SearchForGuaranteedNoMatchAsync($"ZzzNoSuchTokenAnywhereInTheSolution{i}Zzz");
        }

        var listAllResult = await _client.CallToolAsync(
            "ListAll",
            new Dictionary<string, object?>()!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(listAllResult.IsError, Is.Not.True, "ListAll is allowlisted, so it should reach the real tool and succeed even while tripped.");

        // Breaker should now be reset — a previously-blocked, non-allowlisted tool should succeed again.
        var afterReset = await _client.CallToolAsync(
            "ListWorkspaceSolutions",
            new Dictionary<string, object?> { ["workspacePath"] = _fixture.SolutionDirectory }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(afterReset.IsError, Is.Not.True, "A successful allowlisted call while tripped should auto-reset the breaker.");
    }

    [Test]
    public async Task NonZeroMatchSearch_MidStreak_PreventsTripAndNonAllowlistedToolStillWorks()
    {
        await SearchForGuaranteedNoMatchAsync("ZzzNoSuchTokenAnywhereInTheSolution0Zzz");
        await SearchForGuaranteedNoMatchAsync("ZzzNoSuchTokenAnywhereInTheSolution1Zzz");

        // A pattern virtually certain to exist in the sample solution's own source.
        var matchResult = await _client.CallToolAsync(
            "SearchSolutionText",
            new Dictionary<string, object?> { ["pattern"] = "class", ["searchMode"] = "literal" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(matchResult.IsError, Is.Not.True);

        var notBlocked = await _client.CallToolAsync(
            "ListWorkspaceSolutions",
            new Dictionary<string, object?> { ["workspacePath"] = _fixture.SolutionDirectory }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(notBlocked.IsError, Is.Not.True, "A non-zero-match search mid-streak should reset the streak, so the breaker should never have tripped.");
    }
}
