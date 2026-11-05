using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// proposal_sequence_pager.md slice 6 (Decided 10), end to end: a big result is marshalled by <see cref="ToolResultMarshaller"/>
/// into a temp solution root (mode On), then GetLargeResult pages the stored sequence by unit offset. Also pins that a file
/// stored without a sequence keeps the path flow, and that a sequence page passes back through the marshaller unchanged.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class SequenceLargeResultPagingTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;
    private static readonly MethodInfo AnyMethod = typeof(SequenceLargeResultPagingTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    // The marshaller's inline budget minus its echo reserve: a whole page response must never exceed it.
    private const int PageBudget = 15360 - 2560;

    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private string _tempDir = string.Empty;

    // Test-only payload shapes (DTO-EXEMPT: fixture data, never on the wire in production). Declared order alpha, beta = the interleave order.
    private sealed record TwoLists(List<string> Alpha, List<string> Beta);

    private sealed record SourceText(string Source, int StartLine);

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "SequencePaging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", []));
        _tools = Battery.Basic.TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static void AnyMethodTarget()
    {
    }

    private static int WireLength(object result) =>
        JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object))).Length;

    private ToolResultMarshaller Marshaller() =>
        new(NullLogger<ToolResultMarshaller>.Instance, () => _tempDir, () => ResultMarshallerMode.On);

    // One fixed-width record per index so every record is the same size and a page holds a predictable count.
    private static string Record(string prefix, int index) => $"{prefix}-{index:D4}-" + new string('x', 160);

    private static TwoLists BigTwoLists(int alpha = 120, int beta = 80) =>
        new(Enumerable.Range(0, alpha).Select(i => Record("a", i)).ToList(), Enumerable.Range(0, beta).Select(i => Record("b", i)).ToList());

    // Marshals a result in mode On and returns the first response with its resultId.
    private async Task<(string ResultId, JsonObject First)> MarshalAsync(object result)
    {
        object? marshalled = await Marshaller().MarshalAsync("Test", AnyMethod, result, typeof(object), Wire, CancellationToken.None);
        var first = (JsonObject)JsonNode.Parse((string)marshalled!)!;
        string resultId = first["largeResult"]!["resultId"]!.GetValue<string>();
        return (resultId, first);
    }

    private async Task<SentinelCallToolResult<object>> PageAsync(string resultId, int offset, int? limit = null, string? path = null) =>
        await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: limit, offset: offset, path: path);

    private static EnvelopePageResult PageOf(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (EnvelopePageResult)result.SuccessData!;
    }

    private static List<string> Strings(JsonNode? array) => array is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToList() : [];

    [Test]
    public async Task TwoLists_ChainedToTheEnd_ReturnEveryRecordOnce_Interleaved_WithANextOffsetChain()
    {
        TwoLists source = BigTwoLists();
        var (resultId, first) = await MarshalAsync(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source });
        JsonNode pointer = first["largeResult"]!;
        int total = source.Alpha.Count + source.Beta.Count;
        List<string> alpha = Strings(first["successData"]!["alpha"]);
        List<string> beta = Strings(first["successData"]!["beta"]);
        int nextOffset = pointer["nextOffset"]!.GetValue<int>();
        Assert.That(pointer["returned"]!.GetValue<int>(), Is.EqualTo(alpha.Count + beta.Count));
        Assert.That(nextOffset, Is.EqualTo(alpha.Count + beta.Count), "the first page ends where the cursor starts");

        int pages = 1;
        for (int guard = 0; guard < 100; guard++)
        {
            var result = await PageAsync(resultId, nextOffset);
            EnvelopePageResult page = PageOf(result);
            Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget), "a whole page response must fit the marshaller budget");
            Assert.That(page.Offset, Is.EqualTo(nextOffset));
            Assert.That(page.Total, Is.EqualTo(total));
            Assert.That(page.Unit, Is.EqualTo("records"));
            Assert.That(page.Items, Is.Null);
            Assert.That(page.Text, Is.Null);
            Assert.That(result.NextOffset, Is.Null, "no second cursor at the envelope level");
            Assert.That(result.TotalRecords, Is.Null);

            JsonNode shape = page.Envelope!["successData"]!;
            List<string> a = Strings(shape["alpha"]);
            List<string> b = Strings(shape["beta"]);
            Assert.That(page.Returned, Is.EqualTo(a.Count + b.Count));
            if (page.Offset + page.Returned <= 2 * source.Beta.Count)
            {
                Assert.That(Math.Abs(a.Count - b.Count), Is.LessThanOrEqualTo(1), "while both lists last, a page alternates between them");
            }

            alpha.AddRange(a);
            beta.AddRange(b);
            pages++;
            if (!page.HasMoreData)
            {
                Assert.That(page.NextOffset, Is.Null, "the last page has no nextOffset");
                Assert.That(result.HasMoreData, Is.False);
                Assert.That(result.StatusMessage, Does.StartWith("COMPLETE:").And.Contain("nothing more to fetch"));
                break;
            }

            Assert.That(page.NextOffset, Is.EqualTo(page.Offset + page.Returned));
            Assert.That(result.HasMoreData, Is.True);
            Assert.That(result.StatusMessage, Does.StartWith("INCOMPLETE:").And.Contain("GetLargeResult(").And.Contain($"offset: {page.NextOffset}"));
            nextOffset = page.NextOffset!.Value;
        }

        Assert.That(pages, Is.GreaterThan(2), "the fixture must need several pages");
        Assert.That(alpha, Is.EqualTo(source.Alpha), "every alpha record exactly once, in order");
        Assert.That(beta, Is.EqualTo(source.Beta), "every beta record exactly once, in order");
    }

    [Test]
    public async Task Lines_ChainedToTheEnd_ConcatenateToTheOriginalText()
    {
        string text = string.Concat(Enumerable.Range(0, 700).Select(i => $"line {i:D4} " + new string('y', 40) + "\n"));
        var (resultId, first) = await MarshalAsync(new SentinelCallToolResult<SourceText> { IsError = false, SuccessData = new SourceText(text, StartLine: 100) });
        JsonNode pointer = first["largeResult"]!;
        Assert.That(pointer["message"]!.GetValue<string>(), Does.Contain("file lines 100-"));

        var collected = new System.Text.StringBuilder(first["successData"]!["source"]!.GetValue<string>());
        int nextOffset = pointer["nextOffset"]!.GetValue<int>();
        int pages = 1;
        for (int guard = 0; guard < 100; guard++)
        {
            var result = await PageAsync(resultId, nextOffset);
            EnvelopePageResult page = PageOf(result);
            Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget));
            Assert.That(page.Unit, Is.EqualTo("lines"));
            Assert.That(page.Total, Is.EqualTo(700));
            Assert.That(page.Offset, Is.EqualTo(nextOffset));
            Assert.That(result.StatusMessage, Does.Contain($"(file lines {100 + page.Offset}-{100 + page.Offset + page.Returned - 1})"));
            string chunk = page.Envelope!["successData"]!["source"]!.GetValue<string>();
            Assert.That(chunk.Count(c => c == '\n'), Is.EqualTo(page.Returned), "each unit is one whole line");
            collected.Append(chunk);
            pages++;
            if (!page.HasMoreData)
            {
                Assert.That(page.NextOffset, Is.Null);
                Assert.That(result.StatusMessage, Does.StartWith("COMPLETE:"));
                break;
            }

            nextOffset = page.NextOffset!.Value;
        }

        Assert.That(pages, Is.GreaterThan(2));
        Assert.That(collected.ToString(), Is.EqualTo(text), "the pages concatenate to the original text");
    }

    [Test]
    public async Task OffsetBeyondTotal_ReturnsAnEmptyCompletePage_NotAnError()
    {
        TwoLists source = BigTwoLists();
        int total = source.Alpha.Count + source.Beta.Count;
        var (resultId, _) = await MarshalAsync(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source });

        var result = await PageAsync(resultId, offset: total + 25);
        EnvelopePageResult page = PageOf(result);

        Assert.That(page.Returned, Is.EqualTo(0));
        Assert.That(page.Total, Is.EqualTo(total));
        Assert.That(page.Offset, Is.EqualTo(total), "the offset is clamped to the end");
        Assert.That(page.HasMoreData, Is.False);
        Assert.That(page.NextOffset, Is.Null);
        Assert.That(result.HasMoreData, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("past the end"));
        JsonNode shape = page.Envelope!["successData"]!;
        Assert.That(Strings(shape["alpha"]), Is.Empty);
        Assert.That(Strings(shape["beta"]), Is.Empty);
    }

    [Test]
    public async Task LimitSmallerThanAPage_ReturnsExactlyThatManyUnits_AndTheChainStillCoversEverythingOnce()
    {
        TwoLists source = BigTwoLists();
        var (resultId, _) = await MarshalAsync(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source });
        int total = source.Alpha.Count + source.Beta.Count;

        var alpha = new List<string>();
        var beta = new List<string>();
        int offset = 0;
        int pages = 0;
        for (int guard = 0; guard < 100; guard++)
        {
            var result = await PageAsync(resultId, offset, limit: 7);
            EnvelopePageResult page = PageOf(result);
            Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget));
            Assert.That(page.Returned, Is.EqualTo(Math.Min(7, total - offset)), "limit caps the page below what would fit");
            Assert.That(page.Offset, Is.EqualTo(offset));
            JsonNode shape = page.Envelope!["successData"]!;
            alpha.AddRange(Strings(shape["alpha"]));
            beta.AddRange(Strings(shape["beta"]));
            pages++;
            if (!page.HasMoreData)
            {
                break;
            }

            offset = page.NextOffset!.Value;
        }

        Assert.That(pages, Is.EqualTo((total + 6) / 7));
        Assert.That(alpha, Is.EqualTo(source.Alpha));
        Assert.That(beta, Is.EqualTo(source.Beta));
    }

    [Test]
    public async Task PathOnASequenceResult_IsIgnored_WithANoteThatOffsetCountsSequenceUnits()
    {
        TwoLists source = BigTwoLists();
        var (resultId, first) = await MarshalAsync(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source });
        int nextOffset = first["largeResult"]!["nextOffset"]!.GetValue<int>();

        var result = await PageAsync(resultId, nextOffset, path: "successData.alpha");
        EnvelopePageResult page = PageOf(result);

        Assert.That(page.Envelope, Is.Not.Null, "still paged by sequence");
        Assert.That(page.Offset, Is.EqualTo(nextOffset));
        Assert.That(result.StatusMessage, Does.Contain("path 'successData.alpha' was ignored").And.Contain("offset counts records"));
    }

    [Test]
    public async Task FileWithoutASequence_KeepsThePathFlow_IncludingTheEnvelopeLevelCursor()
    {
        // A file written the old way: ResultPager.Fit cuts, StoreEnvelopeAsync records omitted but no Sequence.
        TwoLists source = BigTwoLists();
        string text = JsonSerializer.Serialize(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source }, Wire);
        var (_, omitted) = ResultPager.Fit(JsonNode.Parse(text)!, PageBudget, Wire, successDataType: null);
        var (offloaded, _, storedId) = await LargeResultHelper.StoreEnvelopeAsync(text, _tempDir, CancellationToken.None, omitted);
        Assert.That(offloaded, Is.True);
        string path = omitted[0].Path;

        var result = await PageAsync(storedId!, offset: 0, limit: 20, path: path);
        EnvelopePageResult page = PageOf(result);

        Assert.That(page.Envelope, Is.Null, "path pages are not envelope-shaped");
        Assert.That(page.Path, Is.EqualTo(path));
        Assert.That(page.Unit, Is.EqualTo("items"));
        Assert.That(page.Returned, Is.EqualTo(20));
        Assert.That(page.Items!.Count, Is.EqualTo(20));
        Assert.That(page.NextOffset, Is.EqualTo(20));
        Assert.That(result.NextOffset, Is.EqualTo(20), "path-based pages keep the envelope-level cursor");
        Assert.That(result.HasMoreData, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(page.Total));
        Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget));
    }

    [Test]
    public async Task SequencePages_PassBackThroughTheMarshaller_Unchanged_WithoutANewResultId()
    {
        TwoLists source = BigTwoLists();
        var (resultId, first) = await MarshalAsync(new SentinelCallToolResult<TwoLists> { IsError = false, SuccessData = source });
        int total = source.Alpha.Count + source.Beta.Count;
        int firstReturned = first["largeResult"]!["returned"]!.GetValue<int>();

        // Page sizes the budget allows, plus a full-width limit that BuildPage must still shrink to fit the wrapper.
        foreach (int offset in new[] { firstReturned, 0, 37, total - 3 })
        {
            foreach (int? limit in new int?[] { null, 1000 })
            {
                var result = await PageAsync(resultId, offset, limit);
                string expected = JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object)));

                object? marshalled = await Marshaller().MarshalAsync("GetLargeResult", AnyMethod, result, typeof(object), Wire, CancellationToken.None);

                Assert.That(marshalled, Is.EqualTo(expected), $"offset {offset}, limit {limit}: the marshaller must not re-cut a page");
            }
        }

        // The seeded sequence file is the only one: paging stored nothing new.
        var files = Directory.GetFiles(Path.Combine(_tempDir, ".roslynsentinel", "largeresults"), "largeresult_*.json");
        Assert.That(files, Has.Length.EqualTo(1));
    }
}
