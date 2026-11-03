using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// plan_wire_result_marshaller.md Step 5: GetLargeResult pages a stored <see cref="ResultWrapperType.Envelope"/> file by
/// <c>path</c>. The Step 3 goldens (shared with Tests.Server as embedded resources) are stored through
/// <see cref="LargeResultHelper.StoreEnvelopeAsync"/> into a temp solution root and paged to the end.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class EnvelopeLargeResultPagingTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;
    private static readonly MethodInfo AnyMethod = typeof(EnvelopeLargeResultPagingTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    // The marshaller's inline budget minus its echo reserve: a page must never exceed it.
    private const int PageBudget = 15360 - 2560;

    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "EnvelopePaging_" + Guid.NewGuid().ToString("N"));
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

    private static JsonNode LoadGolden(string fileName)
    {
        var assembly = typeof(EnvelopeLargeResultPagingTests).Assembly;
        using var stream = assembly.GetManifestResourceStream("Golden." + fileName)!;
        return JsonNode.Parse(stream)!;
    }

    private async Task<string> StoreAsync(JsonNode envelope, IReadOnlyList<OmittedPart>? omitted)
    {
        var (offloaded, _, resultId) = await LargeResultHelper.StoreEnvelopeAsync(envelope.ToJsonString(), _tempDir, CancellationToken.None, omitted);
        Assert.That(offloaded, Is.True);
        Assert.That(resultId, Is.Not.Null.And.Not.Empty);
        return resultId!;
    }

    private async Task<(string ResultId, IReadOnlyList<OmittedPart> Omitted)> StoreGoldenAsync(string fileName, int budget)
    {
        JsonNode original = LoadGolden(fileName);
        var (_, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);
        return (await StoreAsync(original, omitted), omitted);
    }

    private static int WireLength(object result) =>
        JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object))).Length;

    private async Task<SentinelCallToolResult<object>> PageAsync(string resultId, string? path, int offset = 0, int limit = 50) =>
        await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: limit, offset: offset, path: path);

    private static EnvelopePageResult PageOf(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (EnvelopePageResult)result.SuccessData!;
    }

    private async Task<List<EnvelopePageResult>> PageToEndAsync(string resultId, string? path, int limit = 50, int startOffset = 0)
    {
        var pages = new List<EnvelopePageResult>();
        int offset = startOffset;
        for (int guard = 0; guard < 5000; guard++)
        {
            var result = await PageAsync(resultId, path, offset, limit);
            var page = PageOf(result);
            Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget), "a page must fit the marshaller budget");
            Assert.That(page.Returned, Is.GreaterThan(0), "no element of a golden is larger than a page");
            Assert.That(result.WarningDetails ?? string.Empty, Does.Not.Contain("larger than one page").And.Not.Contain("longer than one page"));
            pages.Add(page);
            if (!page.HasMoreData)
            {
                return pages;
            }

            offset = page.NextOffset!.Value;
        }

        Assert.Fail("paging did not terminate");
        return pages;
    }

    private static void AssertPagesEqualNode(List<EnvelopePageResult> pages, JsonNode node, string path)
    {
        Assert.That(pages.Select(p => p.Path).Distinct(), Is.EqualTo(new[] { path }));
        Assert.That(pages.Select(p => p.Total).Distinct().Single(), Is.EqualTo(node is JsonArray a ? a.Count : ResultPager.SplitLines(node.GetValue<string>()).Count), "total equals the stored count");
        Assert.That(pages.Sum(p => p.Returned), Is.EqualTo(pages[0].Total), "pages cover the node exactly once");

        if (node is JsonArray stored)
        {
            Assert.That(pages.All(p => p.Unit == "items" && p.Text is null && p.Items is not null), Is.True);
            var joined = new JsonArray(pages.SelectMany(p => p.Items!).Select(n => n?.DeepClone()).ToArray());
            Assert.That(JsonNode.DeepEquals(joined, stored), Is.True, "concatenated items equal the stored array");
        }
        else
        {
            Assert.That(pages.All(p => p.Unit == "lines" && p.Items is null && p.Text is not null), Is.True);
            Assert.That(string.Concat(pages.Select(p => p.Text)), Is.EqualTo(node.GetValue<string>()), "concatenated lines equal the stored string");
        }
    }

    [TestCase("build_2000_errors.json", 20_000)]
    [TestCase("search_300_hits.json", 20_000)]
    [TestCase("git_diff_44kb.json", 12_000)]
    [TestCase("semantic_find_replace_preview_122_sites.json", 15_000)]
    [TestCase("big_error.json", 12_000)]
    public async Task Golden_PagedByEveryOmittedPath_ToTheEnd_EqualsTheStoredNode(string golden, int budget)
    {
        var (resultId, omitted) = await StoreGoldenAsync(golden, budget);
        JsonNode original = LoadGolden(golden);
        Assert.That(omitted, Is.Not.Empty);

        foreach (var part in omitted)
        {
            var pages = await PageToEndAsync(resultId, part.Path);
            var node = ResultPager.SelectNode(original, part.Path)!;
            AssertPagesEqualNode(pages, node, part.Path);
            Assert.That(pages[0].Total, Is.EqualTo(part.Total), "total equals the count the marshaller recorded");
        }
    }

    [Test]
    public async Task DefaultPath_IsTheFirstOmittedPath()
    {
        var (resultId, omitted) = await StoreGoldenAsync("big_error.json", 12_000);

        var page = PageOf(await PageAsync(resultId, path: null));

        Assert.That(omitted.Count, Is.GreaterThan(1));
        Assert.That(page.Path, Is.EqualTo(omitted[0].Path));
        Assert.That(page.Offset, Is.EqualTo(0));
    }

    [Test]
    public async Task DefaultPath_WithoutRecordedOmitted_FallsBackToTheLargestPageableNode()
    {
        string resultId = await StoreAsync(LoadGolden("search_300_hits.json"), omitted: null);

        var page = PageOf(await PageAsync(resultId, path: null));

        Assert.That(page.Path, Is.EqualTo("successData.regexResults"));
        Assert.That(page.Total, Is.EqualTo(300));
    }

    [Test]
    public async Task BadPath_ReturnsNotFound_ListingThePageablePaths_WithoutInternalPaths()
    {
        var (resultId, omitted) = await StoreGoldenAsync("big_error.json", 12_000);

        var result = await PageAsync(resultId, "successData.nope");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("successData.nope"));
        foreach (var part in omitted)
        {
            Assert.That(result.ErrorData.Message, Does.Contain(part.Path));
        }

        Assert.That(result.ErrorData.Message, Does.Not.Contain(_tempDir));
        Assert.That(result.ErrorData.Message, Does.Not.Contain(".roslynsentinel"));
        Assert.That(result.ErrorData.Message, Does.Contain(resultId), "the suggested retry names the result");
    }

    [Test]
    public async Task NonPageableNode_ReturnsInvalidArgument_WithTheAvailablePaths()
    {
        var (resultId, _) = await StoreGoldenAsync("search_300_hits.json", 20_000);

        foreach (string path in new[] { "successData", "successData.regexPatternValid" })
        {
            var result = await PageAsync(resultId, path);

            Assert.That(result.IsError, Is.True, path);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument), path);
            Assert.That(result.ErrorData.Message, Does.Contain("successData.regexResults"), path);
        }
    }

    [Test]
    public async Task OversizedLimit_ShrinksThePage_AndNextOffsetContinuesWithoutGaps()
    {
        var (resultId, _) = await StoreGoldenAsync("search_300_hits.json", 20_000);

        var first = await PageAsync(resultId, "successData.regexResults", limit: 1000);
        var page = PageOf(first);

        Assert.That(page.Returned, Is.InRange(1, 299));
        Assert.That(page.HasMoreData, Is.True);
        Assert.That(page.NextOffset, Is.EqualTo(page.Returned));
        Assert.That(first.WarningDetails, Does.Contain("Page shrunk"));
        Assert.That(WireLength(first), Is.LessThanOrEqualTo(PageBudget));

        var rest = await PageToEndAsync(resultId, "successData.regexResults", startOffset: page.NextOffset!.Value);
        Assert.That(page.Returned + rest.Sum(p => p.Returned), Is.EqualTo(300), "no element is lost or repeated across the shrink");
    }

    [Test]
    public async Task OversizedElement_IsSkippedWithAWarning_AndTheListContinues()
    {
        var envelope = new JsonObject
        {
            ["toolCall"] = new JsonObject { ["toolCallId"] = "abc123", ["name"] = "Probe" },
            ["isError"] = false,
            ["successData"] = new JsonObject
            {
                ["items"] = new JsonArray("small0", new string('x', 30_000), "small2"),
            },
        };
        string resultId = await StoreAsync(envelope, omitted: null);

        var first = PageOf(await PageAsync(resultId, "successData.items"));
        Assert.That(first.Returned, Is.EqualTo(1));
        Assert.That(first.NextOffset, Is.EqualTo(1));

        var second = await PageAsync(resultId, "successData.items", offset: 1);
        var skipped = PageOf(second);
        Assert.That(skipped.Returned, Is.EqualTo(0));
        Assert.That(skipped.NextOffset, Is.EqualTo(2));
        Assert.That(second.WarningDetails, Does.Contain("larger than one page"));
        Assert.That(WireLength(second), Is.LessThanOrEqualTo(PageBudget));

        var third = PageOf(await PageAsync(resultId, "successData.items", offset: 2));
        Assert.That(third.Items!.Single()!.GetValue<string>(), Is.EqualTo("small2"));
        Assert.That(third.HasMoreData, Is.False);
    }

    [Test]
    public async Task Page_SurvivesTheMarshallerInModeOn_Unchanged()
    {
        var (resultId, _) = await StoreGoldenAsync("search_300_hits.json", 20_000);
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance, () => _tempDir, () => ResultMarshallerMode.On);

        foreach (int offset in new[] { 0, 60, 120 })
        {
            var result = await PageAsync(resultId, "successData.regexResults", offset, limit: 1000);
            string expected = JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object)));

            object? marshalled = await marshaller.MarshalAsync("GetLargeResult", AnyMethod, result, typeof(object), Wire, CancellationToken.None);

            Assert.That(marshalled, Is.EqualTo(expected), "the marshaller must not re-cut a page");
        }

        // Only the seeded Envelope file exists: paging stored nothing new.
        var files = Directory.GetFiles(Path.Combine(_tempDir, ".roslynsentinel", "largeresults"), "largeresult_*.json");
        Assert.That(files, Has.Length.EqualTo(1));
    }
}
