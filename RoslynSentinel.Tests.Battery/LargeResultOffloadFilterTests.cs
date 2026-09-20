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
        // callers) -> which is NOT individually wired into ForPossiblyLargeDataAsync -> returns a
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
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise the filter without a loaded solution.");
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
        // list the filter swapped out -> not just replay the pointer. Each page is requested with
        // charLimit == OffloadThresholdBytes (the value the tool's own "continue reading" warning
        // suggests) to confirm the fix in WorkspaceReadNavigationImpl.GetLargeResult's Raw branch:
        // the effective window is capped below OffloadThresholdBytes to reserve headroom for the
        // JSON envelope, so a full page can never itself be large enough to be re-offloaded by this
        // same filter -> if it were, this loop would see an "offloaded" pointer instead of a "data"
        // page and fail below.
        var reassembled = new System.Text.StringBuilder();
        int? offset = 0;
        var pageCount = 0;
        while (offset is not null)
        {
            var page = await _client.CallToolAsync(
                "GetLargeResult",
                new Dictionary<string, object?> { ["reason"] = "test message", ["resultId"] = resultId, ["offset"] = offset.Value, ["charLimit"] = RoslynSentinel.Common.LargeResultHelper.OffloadThresholdBytes }!,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            Assert.That(page.IsError, Is.Not.True);

            var pageBlocks = page.Content.OfType<TextContentBlock>().ToList();
            Assert.That(pageBlocks, Has.Count.EqualTo(1));
            using var pageDoc = JsonDocument.Parse(pageBlocks[0].Text);
            // GetLargeResult is explicitly excluded from the generic offload-pointer wrapper (see
            // ServiceRegistrationExtensionsBasic.cs's "if (context.Params?.Name == \"GetLargeResult\")
            // { return result; }" - docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md),
            // so its own response is never re-shaped into {offloaded, data}; it always returns its
            // native {successDetails: {...}} shape directly, even when that response is itself large.
            Assert.That(pageDoc.RootElement.TryGetProperty("successDetails", out _), Is.True,
                $"GetLargeResult must return its native successDetails shape, not an offload pointer (page was: {pageBlocks[0].Text})");
            var data = pageDoc.RootElement.GetProperty("successDetails");
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
            "A response under threshold must pass through unmodified - the filter must stay a pure pass-through for small tool results.");
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task TypedBranchOffload_GetLargeResultFirstCall_ReturnsDataNotAnotherOffloadEnvelope()
    {
        // Regression test for the originally reported bug (see
        // docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md,
        // now closed): QuerySymbolRelationships offloads through the SymbolRelationshipResultList
        // branch of GetLargeResult (WorkspaceReadNavigationImpl.cs), which - unlike the Raw branch
        // exercised above - previously did a bare Skip(offset).Take(limit) with no size
        // verification, so the default limit=50 page still serialized past the threshold and the
        // filter re-offloaded GetLargeResult's own response under a brand-new resultId with no way
        // for the caller to converge. Seed enough implementations of a shared interface to force an
        // offload, then confirm the very first GetLargeResult call at the default limit returns real
        // "data", not another "offloaded" pointer.
        //
        // Uses searchKind "implementorsOf" rather than "attributeUsages" - both offload through the
        // same SymbolRelationshipResultList branch, but attributeUsages currently crashes with
        // InvalidOperationException: Sequence contains no elements for freshly-seeded types
        // (DiscoveryEngine.FindAttributeUsagesAsync's unchecked .First() on an unresolved
        // LocateSymbolAsync lookup) - a separate, already-tracked defect, see
        // docs/current/blockers/blocking_error_findattributeusages_first_throws_on_unresolved_target.md.
        // implementorsOf resolves purely via symbol search (FindAllImplementationsAsync), with no
        // dependency on that lookup path.
        const int implementationCount = 400;
        var implementations = Enumerable.Range(0, implementationCount)
            .Select(i => $"public class OffloadFilterProbeImpl{i} : IOffloadFilterProbeTarget {{ }}");
        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public interface IOffloadFilterProbeTarget { }" + Environment.NewLine +
                     string.Join(Environment.NewLine, implementations) + Environment.NewLine;

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "OffloadFilterImplProbe.cs"),
                source,
                reloadSolution: false);
        }

        // forceReload:true is required here - LoadSolution is a no-op by default when the exact
        // same solutionPath is already loaded (which it is, from SetUp), so without this flag the
        // newly-seeded file would silently never be read from disk.
        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath, ["forceReload"] = true }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Reload after seeding the implementor probe file must succeed.");

        var queryResult = await _client.CallToolAsync(
            "QuerySymbolRelationships",
            new Dictionary<string, object?> { ["reason"] = "test message", ["name"] = "IOffloadFilterProbeTarget", ["searchKind"] = "implementorsOf" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        var queryText = string.Join(" ", queryResult.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(queryResult.IsError, Is.Not.True, $"The lookup itself must succeed even though its result is large enough to be offloaded. Got: {queryText}");

        var blocks = queryResult.Content.OfType<TextContentBlock>().ToList();
        Assert.That(blocks, Has.Count.EqualTo(1), "An offloaded response must be replaced with a single small pointer block.");

        // QuerySymbolRelationships is wired into the typed ForPossiblyLargeDataAsync/LargeResultInfo
        // path (see the "coexists with, does not replace" note on the generic filter), so an
        // offloaded response here carries the pointer under a nested "largeResult" object, not the
        // generic filter's top-level "offloaded"/"resultId" shape used for tools that AREN'T
        // individually wired in (e.g. FindReferences, exercised by the sibling test above).
        using var pointer = JsonDocument.Parse(blocks[0].Text);
        Assert.That(pointer.RootElement.TryGetProperty("largeResult", out var largeResult), Is.True,
            $"Expected this query to be large enough to trigger the typed offload path; got: {blocks[0].Text}");
        var resultId = largeResult.GetProperty("resultId").GetString();
        Assert.That(resultId, Is.Not.Null.And.Not.Empty);

        var page = await _client.CallToolAsync(
            "GetLargeResult",
            new Dictionary<string, object?> { ["reason"] = "test message", ["resultId"] = resultId }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(page.IsError, Is.Not.True);

        var pageBlocks = page.Content.OfType<TextContentBlock>().ToList();
        Assert.That(pageBlocks, Has.Count.EqualTo(1));
        using var pageDoc = JsonDocument.Parse(pageBlocks[0].Text);
        // GetLargeResult is explicitly excluded from the generic offload-pointer wrapper (see
        // ServiceRegistrationExtensionsBasic.cs's "if (context.Params?.Name == \"GetLargeResult\")
        // { return result; }"), so its own response is never re-shaped into {offloaded, data}; it
        // always returns its native {successDetails: [...]} shape directly.
        Assert.That(pageDoc.RootElement.TryGetProperty("successDetails", out var data), Is.True,
            $"GetLargeResult's first call on a typed (non-Raw) branch must return actual records, not another offload envelope. Got: {pageBlocks[0].Text}");
        Assert.That(data.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(data.GetArrayLength(), Is.GreaterThan(0), "The returned page must contain at least one real record.");
    }
}
