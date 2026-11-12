using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Miss text of WorkspaceReadNavigationImpl: a page path that selects nothing in a stored result (PageEnvelope, ranked pageable paths with a
// prefilled fetch call) and a GetLargeResult call that resolves to no stored result file (the resultId or filePath asked for, which check
// failed, how many results are stored). Plan_not_found_error_orientation, Step 5e slice 5. The golden search_300_hits.json (embedded in this
// assembly) is stored through LargeResultHelper.StoreEnvelopeAsync into a temp solution root, exactly as EnvelopeLargeResultPagingTests does.
[TestFixture]
[Category("WorkspaceTools")]
public sealed class LargeResultMissTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "LargeResultMiss_" + Guid.NewGuid().ToString("N"));
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

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("Supply a valid"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private async Task<string> StoreGoldenAsync()
    {
        JsonNode original;
        using (var stream = typeof(LargeResultMissTests).Assembly.GetManifestResourceStream("Golden.search_300_hits.json")!)
        {
            original = JsonNode.Parse(stream)!;
        }

        var (_, omitted) = ResultPager.Fit(original, 20_000, Wire, successDataType: null);
        var (offloaded, _, resultId) = await LargeResultHelper.StoreEnvelopeAsync(original.ToJsonString(), _tempDir, CancellationToken.None, omitted);
        Assert.That(offloaded, Is.True);
        return resultId!;
    }

    private string StoredFileCountText(int count) => $"holds {count} stored result(s).";

    // ---- PageEnvelope: path that selects nothing ----

    [Test]
    public async Task PagedRead_MisspeltPath_OffersTheNearPathWithAPrefilledFetchCallAsync()
    {
        var resultId = await StoreGoldenAsync();

        var result = await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, path: "successData.regexResult");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("Path 'successData.regexResult' was not found in the stored result"));
        Assert.That(message, Does.Contain($"Did you mean 'successData.regexResults'? Retry with {LargeResultHints.FetchCall(resultId, offset: null, path: "successData.regexResults")}."));
        Assert.That(message, Does.Contain("Pageable paths: "));
        Assert.That(message, Does.Not.Contain(_tempDir), "No internal path leaks.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task PagedRead_NoSimilarPath_StatesTheNoCandidateFactAndNamesAFetchCallForTheFirstPathAsync()
    {
        var resultId = await StoreGoldenAsync();

        var result = await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, path: "zzz.qqq");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No pageable path has a name similar to 'zzz.qqq'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("pages the first pageable path."));
        Assert.That(message, Does.Contain("Pageable paths: "));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- GetLargeResult: no stored result file ----

    [Test]
    public async Task GetLargeResult_UnknownResultIdWithOneStoredResult_StatesTheIdTheCountAndTheMostRecentIdAsync()
    {
        var storedId = await StoreGoldenAsync();
        var stored = Directory.EnumerateFiles(Path.Combine(_tempDir, ".roslynsentinel", "largeresults")).ToList();

        var result = await _tools.GetLargeResultTool(reason: "test message", resultId: "00000000000000000000000000000000");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No stored result matches resultId '00000000000000000000000000000000'."));
        Assert.That(message, Does.Contain(StoredFileCountText(1)));
        Assert.That(message, Does.Contain($"Most recent resultId: '{storedId}'; {LargeResultHints.FetchCall(storedId)} pages it."));
        AssertFactsAndOptionsOnly(message);
        Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, ".roslynsentinel", "largeresults")).ToList(), Is.EqualTo(stored), "A refused call must not touch the stored results.");
    }

    [Test]
    public async Task GetLargeResult_UnknownResultIdWithNoStoredResults_StatesTheCountAndOffersNoRecentIdAsync()
    {
        var result = await _tools.GetLargeResultTool(reason: "test message", resultId: "00000000000000000000000000000000");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("No stored result matches resultId '00000000000000000000000000000000'."));
        Assert.That(message, Does.Contain(StoredFileCountText(0)));
        Assert.That(message, Does.Not.Contain("Most recent resultId"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task GetLargeResult_FilePathOutsideTheDirectory_StatesWhichChecksFailedAsync()
    {
        var outside = Path.Combine(_tempDir, "result_20260101T000000Z_fakeid.json");
        await File.WriteAllTextAsync(outside, "{}");

        var result = await _tools.GetLargeResultTool(reason: "test message", filePath: outside);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)));
        Assert.That(result.ErrorData.Message, Does.Contain($"filePath '{outside}' is not a stored result: it is outside the largeresults directory; its file name does not match largeresult_*.json."));
        AssertFactsAndOptionsOnly(result.ErrorData.Message);
        Assert.That(File.Exists(outside), Is.True, "A refused call must leave the file alone.");
    }

    [Test]
    public async Task GetLargeResult_FilePathInTheDirectoryButMissing_StatesNoFileExistsAsync()
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var missing = Path.Combine(dir, "largeresult_20260101T000000Z_deadbeef.json");

        var result = await _tools.GetLargeResultTool(reason: "test message", filePath: missing);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain($"filePath '{missing}' is not a stored result: no file exists at that path."));
        Assert.That(result.ErrorData.Message, Does.Contain(StoredFileCountText(0)));
        AssertFactsAndOptionsOnly(result.ErrorData.Message);
    }

    [Test]
    public async Task GetLargeResult_NeitherResultIdNorFilePath_StatesNeitherWasGivenAsync()
    {
        var result = await _tools.GetLargeResultTool(reason: "test message");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Neither resultId nor filePath was given."));
        AssertFactsAndOptionsOnly(result.ErrorData.Message);
    }
}
