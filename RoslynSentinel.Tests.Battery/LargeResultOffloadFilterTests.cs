using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class LargeResultOffloadFilterTests
{
    // Added by AddMember (expected - used for diagnostics)

    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };
    // Added by InsertMemberAfter (expected - used for diagnostics)
    // LargeResultHelper.OffloadThresholdBytes is 30 * 1024. Each CallerInfo record (CallerMethod,
    // CallerType, absolute FilePath under a temp dir, Line, CodeSnippet) serializes to well over
    // 100 bytes, so this many distinct callers of Target() comfortably exceeds the threshold
    // regardless of the sample solution's own (small) content.
    private const int CallerCount = 400;    // Added by AddMember (expected - used for diagnostics)
                                            // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string CallerMethodPrefix = "OffloadFilterCaller";
    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        // SearchSolutionText/ListSolutionItems/etc. are already wired into the typed
        // ForPossiblyLargeDataAsync/LargeResultInfo offload path (see
        // docs/current/proposal_centralized_large_result_filter.md's "coexists with, does not
        // replace" note), so by the time their response reaches the generic filter under test here
        // it has already been shrunk to a small pointer and can never exercise this filter. Instead
        // seed a target method plus many tiny distinct callers of it, so FindReferences(kind:
        // callers) — which is NOT individually wired into ForPossiblyLargeDataAsync — returns a
        // flat CallerInfo list long enough to exceed OffloadThresholdBytes on its own.
        var callers = Enumerable.Range(0, CallerCount)
            .Select(i => $"    public void {CallerMethodPrefix}{i}() {{ Target(); }}");
        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class OffloadFilterProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     "    public void Target() { }" + Environment.NewLine +
                     string.Join(Environment.NewLine, callers) + Environment.NewLine +
                     "}" + Environment.NewLine;

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "OffloadFilterProbe.cs"),
                source,
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
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load — cannot exercise the filter without a loaded solution.");
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }
    [Test]
    public async Task OversizedToolResponse_GetsOffloaded_AndGetLargeResultRoundTripsIt()
    {
        var findResult = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?> { ["reason"] = "test message", ["symbolName"] = "Target", ["kind"] = "callers" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(findResult.IsError, Is.Not.True, "The lookup itself must succeed even though its result is large enough to be offloaded.");

        var blocks = findResult.Content.OfType<TextContentBlock>().ToList();
        Assert.That(blocks, Has.Count.EqualTo(1), "An offloaded response must be replaced with a single small pointer block.");

        using var pointer = JsonDocument.Parse(blocks[0].Text);
        Assert.That(pointer.RootElement.GetProperty("offloaded").GetBoolean(), Is.True);
        var resultId = pointer.RootElement.GetProperty("resultId").GetString();
        Assert.That(resultId, Is.Not.Null.And.Not.Empty);
        var sizeBytes = pointer.RootElement.GetProperty("sizeBytes").GetInt32();
        Assert.That(sizeBytes, Is.GreaterThan(RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes),
            "sizeBytes should report the size that actually triggered the offload.");

        // Round-trip: GetLargeResult must be able to read the offloaded payload back, and the
        // reassembled text (paged, since Raw pages by byte-window) must contain the real caller
        // list the filter swapped out — not just replay the pointer. Each page is requested with
        // limit == OffloadThresholdBytes (the value the tool's own "continue reading" warning
        // suggests) to confirm the fix in WorkspaceReadNavigationImpl.GetLargeResult's Raw branch:
        // the effective window is capped below OffloadThresholdBytes to reserve headroom for the
        // JSON envelope, so a full page can never itself be large enough to be re-offloaded by this
        // same filter — if it were, this loop would see an "offloaded" pointer instead of a "data"
        // page and fail below.
        var reassembled = new System.Text.StringBuilder();
        int? offset = 0;
        var pageCount = 0;
        while (offset is not null)
        {
            var page = await _client.CallToolAsync(
                "GetLargeResult",
                new Dictionary<string, object?> { ["reason"] = "test message", ["resultId"] = resultId, ["offset"] = offset.Value, ["limit"] = RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes }!,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            Assert.That(page.IsError, Is.Not.True);

            var pageBlocks = page.Content.OfType<TextContentBlock>().ToList();
            Assert.That(pageBlocks, Has.Count.EqualTo(1));
            using var pageDoc = JsonDocument.Parse(pageBlocks[0].Text);
            Assert.That(pageDoc.RootElement.TryGetProperty("data", out _), Is.True,
                $"GetLargeResult's own response must never itself be large enough to be re-offloaded (page was: {pageBlocks[0].Text})");
            var data = pageDoc.RootElement.GetProperty("data");
            reassembled.Append(data.GetProperty("text").GetString());

            offset = data.TryGetProperty("nextOffset", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.GetInt32()
                : null;
            pageCount++;
            Assert.That(pageCount, Is.LessThan(20), "Paging should terminate; too many pages indicates a broken offset/hasMore computation.");
        }

        Assert.That(reassembled.ToString(), Does.Contain($"{CallerMethodPrefix}0"),
            "The reassembled offloaded text must contain the real caller list, not just the pointer.");
        Assert.That(reassembled.ToString(), Does.Contain($"{CallerMethodPrefix}{CallerCount - 1}"));
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

    [Test]
    public async Task UnderThresholdToolResponse_PassesThroughUnchanged()
    {
        var result = await _client.CallToolAsync(
            "ListAll",
            new Dictionary<string, object?> { ["reason"] = "test message" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Does.Not.Contain("\"offloaded\":true"),
            "A response under threshold must pass through unmodified — the filter must stay a pure pass-through for small tool results.");
    }
}
