using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // flips the process-wide ROSLYNSENTINEL_RESULT_MARSHALLER env var in SetUp/TearDown
public class LargeResultOffloadFilterTests
{
    // These tests cover the generic offload filter, which stays the backstop until plan_wire_result_marshaller.md Step 10. With the
    // marshaller default on (Step 6) a hooked tool's oversized result is fitted before the filter sees it, so the fixture pins mode off.
    private string? _previousMarshallerMode;

    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };
    // LargeResultHelper.OffloadThresholdBytes is 15 * 1024. Each CallerInfo record (CallerMethod,
    // CallerType, absolute FilePath under a temp dir, Line, CodeSnippet) serializes to well over
    // 100 bytes, so this many distinct callers of Target() comfortably exceeds the threshold
    // regardless of the sample solution's own (small) content.
    private const int CallerCount = 400; private const string CallerMethodPrefix = "OffloadFilterCaller";
    [SetUp]
    public async Task SetUp()
    {
        _previousMarshallerMode = Environment.GetEnvironmentVariable(RoslynSentinel.Common.ResultMarshallerOptions.EnvVarName);
        Environment.SetEnvironmentVariable(RoslynSentinel.Common.ResultMarshallerOptions.EnvVarName, "off");
        _fixture = new TestSolutionFixture();

        // Seed a target method plus many tiny distinct callers of it, so FindReferencesTool(kind:
        // callers) returns a flat CallerInfo list long enough to exceed OffloadThresholdBytes on its
        // own. Mode off makes the typed ForPossiblyLargeDataAsync path a pass-through (it stores
        // nothing), so every oversized response reaches the generic filter under test here.
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

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
        Environment.SetEnvironmentVariable(RoslynSentinel.Common.ResultMarshallerOptions.EnvVarName, _previousMarshallerMode);
    }

    // Pages a stored backstop result to its end through GetLargeResult with offset only: the backstop now stores a marshaller-compatible
    // Envelope, so the Envelope reader answers {path, offset, returned, total, unit, nextOffset, hasMoreData, items|text} (paged on the
    // largest array or string, inferred). Every page must be a native page, never another offload stub or a second largeResult pointer.
    private async Task<(string Unit, int Total, int ReturnedSum, int ItemCount, string Reassembled, int PageCount)> PageStoredResultToEndAsync(string resultId)
    {
        var unit = string.Empty;
        var total = 0;
        var returnedSum = 0;
        var itemCount = 0;
        var pageCount = 0;
        var reassembled = new System.Text.StringBuilder();
        int? offset = 0;
        while (offset is not null)
        {
            var page = await _client.CallToolAsync(
                "GetLargeResult",
                new Dictionary<string, object?> { ["reason"] = "test message", ["resultId"] = resultId, ["offset"] = offset.Value }!,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            Assert.That(page.IsError, Is.Not.True);

            var pageBlocks = page.Content.OfType<TextContentBlock>().ToList();
            Assert.That(pageBlocks, Has.Count.EqualTo(1));
            using var pageDoc = JsonDocument.Parse(pageBlocks[0].Text);
            Assert.That(pageDoc.RootElement.TryGetProperty("offloaded", out _), Is.False,
                $"A GetLargeResult page must not itself be stored again (second-level pointer). Page was: {pageBlocks[0].Text}");
            Assert.That(pageDoc.RootElement.TryGetProperty("largeResult", out _), Is.False,
                $"A GetLargeResult page must not carry a largeResult pointer. Page was: {pageBlocks[0].Text}");
            Assert.That(pageDoc.RootElement.TryGetProperty("successData", out var data), Is.True,
                $"GetLargeResult must return its native successData shape (page was: {pageBlocks[0].Text})");

            unit = data.GetProperty("unit").GetString()!;
            total = data.GetProperty("total").GetInt32();
            returnedSum += data.GetProperty("returned").GetInt32();
            if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                itemCount += items.GetArrayLength();
                reassembled.Append(items.GetRawText());
            }
            else
            {
                reassembled.Append(data.GetProperty("text").GetString());
            }

            offset = data.TryGetProperty("nextOffset", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.GetInt32()
                : null;
            pageCount++;
            Assert.That(pageCount, Is.LessThan(100), "Paging should terminate; too many pages indicates a broken offset/hasMore computation.");
        }

        return (unit, total, returnedSum, itemCount, reassembled.ToString(), pageCount);
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
        Assert.That(sizeBytes, Is.GreaterThan(LargeResultHelper.InlineBudgetChars),
            "sizeBytes should report the size that actually triggered the offload.");

        // The backstop stores the same wrapper type the marshaller does, so the Envelope reader (not the Raw char window) answers.
        var storedFile = Directory.GetFiles(Path.Combine(_fixture.SolutionDirectory, ".roslynsentinel", "largeresults"), $"largeresult_*_{resultId}.json").Single();
        using (var stored = JsonDocument.Parse(File.ReadAllText(storedFile)))
        {
            Assert.That(stored.RootElement.GetProperty("Type").GetString(), Is.EqualTo(nameof(ResultWrapperType.Envelope)),
                "The request-filter backstop must store an Envelope, not a Raw wrapper.");
        }

        // Round-trip: GetLargeResult pages the stored envelope on its largest array (inferred, no path); the pages
        // must cover the whole caller list the filter swapped out, not just replay the pointer. PageStoredResultToEndAsync also fails
        // the test if any page is itself an offload stub or carries a second largeResult pointer.
        var paged = await PageStoredResultToEndAsync(resultId!);

        Assert.That(paged.Unit, Is.EqualTo("items"));
        Assert.That(paged.PageCount, Is.GreaterThan(1), "The payload is larger than the inline budget, so it needs more than one page.");
        Assert.That(paged.ReturnedSum, Is.EqualTo(paged.Total), "The pages must cover the paged array exactly once.");
        Assert.That(paged.ItemCount, Is.EqualTo(paged.Total));
        Assert.That(paged.Total, Is.GreaterThanOrEqualTo(CallerCount));
        Assert.That(paged.Reassembled, Does.Contain($"{CallerMethodPrefix}0\""),
            "The reassembled offloaded items must contain the real caller list, not just the pointer.");
        Assert.That(paged.Reassembled, Does.Contain($"{CallerMethodPrefix}{CallerCount - 1}\""));
    }

    [Test]
    public async Task GetLargeResult_OversizeUnitPage_IsNotStoredAgain()
    {
        // A page of a Sequence-stored file goes out over budget when one unit alone is larger than a page (proposal_sequence_pager.md
        // Decided 4). The marshaller flags that (OversizeUnitSignal) only in mode On; this fixture runs mode off, so the only thing that
        // keeps the backstop from storing the page under a NEW resultId (and starting a fetch/store loop) is its GetLargeResult exemption.
        // Remove the exemption and this test fails with an offload stub in place of the page.
        var oversizeBlob = new string('x', LargeResultHelper.InlineBudgetChars + 2000);
        var wire = JsonSerializer.Serialize(new
        {
            isError = false,
            successData = new
            {
                items = new object[] { new { blob = oversizeBlob }, new { small = 2 } }
            },
        });
        var sequence = new SequenceDescriptor(SequenceKind.Lists, ["successData.items"], PagingOrder.Interleaved, 0);
        var (offloaded, _, resultId) = await LargeResultHelper.StoreEnvelopeAsync(wire, _fixture.SolutionDirectory, TestContext.CurrentContext.CancellationToken, omitted: null, sequence: sequence);
        Assert.That(offloaded, Is.True, "Precondition: the oversize-unit envelope must be stored.");

        var page = await _client.CallToolAsync(
            "GetLargeResult",
            new Dictionary<string, object?> { ["reason"] = "test message", ["resultId"] = resultId }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(page.IsError, Is.Not.True);

        var pageBlocks = page.Content.OfType<TextContentBlock>().ToList();
        Assert.That(pageBlocks, Has.Count.EqualTo(1));
        using var pageDoc = JsonDocument.Parse(pageBlocks[0].Text);
        Assert.That(pageDoc.RootElement.TryGetProperty("offloaded", out _), Is.False,
            $"An oversize-unit GetLargeResult page must pass through, not be stored under a second resultId. Got: {pageBlocks[0].Text}");
        Assert.That(pageBlocks[0].Text.Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars),
            "Precondition: the page must be over the offload threshold, otherwise this test pins nothing.");
        var data = pageDoc.RootElement.GetProperty("successData");
        Assert.That(data.GetProperty("returned").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("hasMoreData").GetBoolean(), Is.True);
        Assert.That(data.GetProperty("nextOffset").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("envelope").GetProperty("successData").GetProperty("items").GetArrayLength(), Is.EqualTo(1));
    }

    [Test]
    public async Task OffloadStub_CarriesSolutionRoot()
    {
        var findResult = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?> { ["reason"] = "test message", ["symbolName"] = "Target", ["kind"] = "callers" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(findResult.IsError, Is.Not.True);

        var blocks = findResult.Content.OfType<TextContentBlock>().ToList();
        Assert.That(blocks, Has.Count.EqualTo(1));

        using var pointer = JsonDocument.Parse(blocks[0].Text);
        var solutionRoot = pointer.RootElement.GetProperty("solutionRoot").GetString();
        Assert.That(solutionRoot, Is.Not.Null.And.Not.Empty, "The offload stub must carry solutionRoot for relative-path resolution.");
        Assert.That(solutionRoot, Is.EqualTo(_fixture.SolutionDirectory));
    }

    [Test]
    public async Task UnderThresholdToolResponse_PassesThroughUnchanged()
    {
        // A narrow ListAll (kind class, one project) lists only the seeded OffloadFilterProbe, far below
        // OffloadThresholdBytes, so the filter must leave its response untouched.
        var result = await _client.CallToolAsync(
            "ListAll",
            new Dictionary<string, object?> { ["reason"] = "test message", ["kind"] = "class", ["projectName"] = "ContosoOrders.Core" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(text, Does.Not.Contain("\"offloaded\":true"),
            "A response under threshold must pass through unmodified - the filter must stay a pure pass-through for small tool results.");

        using var doc = JsonDocument.Parse(text);
        var hasPayload = doc.RootElement.TryGetProperty("successData", out _) || doc.RootElement.TryGetProperty("largeResult", out _);
        Assert.That(hasPayload, Is.True, $"Under-threshold response carries neither successData nor largeResult: {text}");
    }

    [Test]
    public async Task ImplementorsQuery_ModeOff_OffloadsThroughGenericFilter_FirstGetLargeResultCallReturnsData()
    {
        // Regression test for the reported re-offload loop (see
        // docs/current/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md, closed).
        // With the marshaller off, QuerySymbolRelationshipsTool's typed ForPossiblyLargeDataAsync is a
        // pass-through, so an oversized implementorsOf result reaches the generic filter and is stored
        // as an Envelope. Seed enough implementations of a shared interface to force that offload, then confirm
        // the very first GetLargeResult call at default limits returns real data, not another
        // offload envelope.
        //
        // Uses searchKind "implementorsOf" rather than "attributeUsages" - attributeUsages currently
        // crashes with InvalidOperationException: Sequence contains no elements for freshly-seeded types
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

        // forceReload:true is required here - LoadSolutionTool is a no-op by default when the exact
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

        // The generic filter's stub is top-level: {offloaded:true, resultId, sizeBytes, ...}. There is
        // no nested "largeResult" object here because the typed path stores nothing in mode off.
        using var pointer = JsonDocument.Parse(blocks[0].Text);
        Assert.That(pointer.RootElement.TryGetProperty("offloaded", out var offloaded) && offloaded.GetBoolean(), Is.True,
            $"Expected the generic filter to offload this {implementationCount}-implementor result; got: {blocks[0].Text}");
        var resultId = pointer.RootElement.GetProperty("resultId").GetString();
        Assert.That(resultId, Is.Not.Null.And.Not.Empty);

        // Default limits on purpose: the very first page must already be real data, not another offload stub.
        // The backstop stored an Envelope, so GetLargeResult answers with the Envelope reader's page ({path, offset, returned, total,
        // unit, nextOffset, hasMoreData, items|text}); PageStoredResultToEndAsync fails on any page that is a stub or carries a
        // second largeResult pointer (the former re-offload loop), and the pages together must cover the whole implementor list.
        var paged = await PageStoredResultToEndAsync(resultId!);

        Assert.That(paged.Unit, Is.EqualTo("items"));
        Assert.That(paged.ReturnedSum, Is.EqualTo(paged.Total), "The pages must cover the paged array exactly once.");
        Assert.That(paged.ItemCount, Is.EqualTo(paged.Total));
        Assert.That(paged.Total, Is.GreaterThanOrEqualTo(implementationCount));
        Assert.That(paged.Reassembled, Does.Contain("OffloadFilterProbeImpl0\""),
            "The reassembled pages must contain real implementor records from the stored payload.");
        Assert.That(paged.Reassembled, Does.Contain($"OffloadFilterProbeImpl{implementationCount - 1}\""));
    }
}
