using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for WorkspaceTools.GetLargeResultTool (file resolution, load, the Envelope branch and the old-file error):
///   T1  - No resultId and no filePath -> error "Result file not found"
///   T2  - Unknown resultId (file doesn't exist) -> error
///   T5  - FilePathWrapper inside largeresults directory -> envelope page returned
///   T6  - FilePathWrapper outside largeresults directory -> error
///   T18-T20 - Envelope paging budget/limit behaviour
///   T21 - A file written by an older server (Raw or typed wrapper Type) -> structured "older server version" error
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class GetLargeResultTests
{
    private IWorkspaceManager _workspaceManager;
    //private ScanTools _scanTools;
    private WorkspaceTools _workspaceTools;
    private string _tempDir;

    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "GetLargeResultTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", []));

        _workspaceTools = Battery.Basic.TestTools.WorkspaceTools(_workspaceManager);
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

    private string LargeResultsFilePath(string resultId)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        return Path.Combine(dir, $"largeresult_{ts}_{resultId}.json");
    }

    private string WriteLargeResultFile<T>(T data, ResultWrapperType type, string resultId)
    {
        var filePath = LargeResultsFilePath(resultId);
        var wrapper = new ResultWrapper
        {
            Type = type,
            Data = JsonSerializer.SerializeToNode(data, TestJsonOptions)!,
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(wrapper, TestJsonOptions), new UTF8Encoding(false));
        return filePath;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – No resultId and no filePath -> error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_GetLargeResult_NoResultIdNoFilePath_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResultTool(reason: "test message");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("Neither resultId nor filePath was given."),
            "ErrorData should state that neither a resultId nor a filePath was given.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – Unknown resultId -> error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T2_GetLargeResult_UnknownResultId_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: "00000000000000000000000000000000");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("No stored result matches resultId '00000000000000000000000000000000'."));
    }

    // Deliberately no SetTestSolution or SolutionPath: solution root remains empty.
    private WorkspaceTools CreateWorkspaceToolsWithoutSolution() =>
        Battery.Basic.TestTools.WorkspaceTools(Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance));

    // ══════════════════════════════════════════════════════════════════════════
    // T2b – No solution loaded, unknown resultId -> SolutionNotLoaded error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task GetLargeResult_NoSolutionLoaded_ReturnsSolutionNotLoadedNotFileNotFoundAsync()
    {
        var workspaceTools = CreateWorkspaceToolsWithoutSolution();
        try
        {
            var result = await workspaceTools.GetLargeResultTool(reason: "test message", resultId: "00000000000000000000000000000000");

            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData, Is.Not.Null);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
            Assert.That(result.ErrorData!.Message, Does.Contain("No solution is loaded"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("No stored result matches"));
        }
        finally
        {
            // Dispose the unloaded workspace manager created for this test
            var workspaceManagerField = typeof(WorkspaceTools).GetField("_workspaceManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (workspaceManagerField?.GetValue(workspaceTools) is IWorkspaceManager unloadedWm)
            {
                unloadedWm.Dispose();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – filePath (instead of resultId) inside the largeresults directory resolves the stored Envelope file
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T5_GetLargeResult_ValidFilePath_InLargeResultsDir_ReturnsEnvelopePage()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var filePath = WriteLargeResultFile(new { successData = new { text = "one\ntwo\nthree\n" } }, ResultWrapperType.Envelope, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", filePath: filePath);

        Assert.That(result.IsError, Is.False);
        var returned = (int)result.SuccessData!.GetType().GetProperty("Returned")!.GetValue(result.SuccessData)!;
        Assert.That(returned, Is.EqualTo(3), "filePath must resolve the same stored file resultId would.");
        Assert.That(result.HasMoreData, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – FilePathWrapper outside scans directory -> error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T6_GetLargeResult_FilePathOutsideScansDir_ReturnsError()
    {
        // Write a file that looks like a result file but is outside the largeresults directory.
        var outsidePath = Path.Combine(_tempDir, "result_20260101T000000Z_fakeid.json");
        await File.WriteAllTextAsync(outsidePath, "{}");

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", filePath: outsidePath);

        Assert.That(!result.IsError, Is.False,
            "A result file outside .roslynsentinel/largeresults/ must be rejected.");
        Assert.That(result.IsError, Is.True); Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound)); Assert.That(result.ErrorData!.Message, Does.Contain($"filePath '{outsidePath}' is not a stored result: it is outside the largeresults directory; its file name does not match largeresult_*.json.")); // the message now states both failed checks (outside the directory and the largeresult_ file-name pattern)
    }

    [Test, CancelAfter(10000)]
    public async Task T18_GetLargeResult_Envelope_NoLimit_FillsSizeBudgetWithShortLines()
    {
        // 5,000 two-character lines overflow one page, so the page is bound by the size budget, not by a line count.
        var text = string.Concat(Enumerable.Repeat("x\n", 5000));
        var resultId = Guid.NewGuid().ToString("N");
        WriteLargeResultFile(new { successData = new { text } }, ResultWrapperType.Envelope, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId);

        Assert.That(result.IsError, Is.False);
        var returned = (int)result.SuccessData!.GetType().GetProperty("Returned")!.GetValue(result.SuccessData)!;
        Assert.That(returned, Is.GreaterThan(200), "With no limit the page must fill the size budget, not stop at the old 50-line default.");
        Assert.That(JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object))).Length,
            Is.LessThanOrEqualTo(LargeResultHelper.InlineBudgetChars - LargeResultHelper.EchoReserveChars));
        Assert.That(result.HasMoreData, Is.True);
        Assert.That(result.NextOffset, Is.EqualTo(returned));
    }

    [Test, CancelAfter(10000)]
    public async Task T19_GetLargeResult_Envelope_ExplicitLimit_IsAnUpperBound()
    {
        var text = string.Concat(Enumerable.Repeat("x\n", 5000));
        var resultId = Guid.NewGuid().ToString("N");
        WriteLargeResultFile(new { successData = new { text } }, ResultWrapperType.Envelope, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 10);

        Assert.That(result.IsError, Is.False);
        var returned = (int)result.SuccessData!.GetType().GetProperty("Returned")!.GetValue(result.SuccessData)!;
        Assert.That(returned, Is.EqualTo(10), "An explicit limit is an upper bound and must return exactly that many lines when they fit.");
        Assert.That(result.HasMoreData, Is.True);
        Assert.That(result.NextOffset, Is.EqualTo(10));
    }

    [Test, CancelAfter(10000)]
    public async Task T20_GetLargeResult_Envelope_NoLimitShrunkPage_HasNoShrinkWarning()
    {
        // 200 lines of 200 characters: the budget fits only part of them, so the page shrinks. An omitted limit is not a
        // caller-requested count, so the shrink must not be reported as "Page shrunk".
        var text = string.Concat(Enumerable.Repeat(new string('y', 199) + "\n", 200));
        var resultId = Guid.NewGuid().ToString("N");
        WriteLargeResultFile(new { successData = new { text } }, ResultWrapperType.Envelope, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId);

        Assert.That(result.IsError, Is.False);
        var returned = (int)result.SuccessData!.GetType().GetProperty("Returned")!.GetValue(result.SuccessData)!;
        Assert.That(returned, Is.GreaterThan(0).And.LessThan(200), "The page must shrink to fit the budget for this test to exercise the shrink path.");
        Assert.That(result.HasMoreData, Is.True);
        Assert.That(result.WarningDetails ?? string.Empty, Does.Not.Contain("Page shrunk"));
    }

    [Test, CancelAfter(10000)]
    public async Task GetLargeResult_Envelope_OnlyLargeErrorDataMessage_PagesTheMessageByLinesAsync()
    {
        // A failed call whose only large content is a multi-line errorData.message (about 30 KB, 600 lines). ResultPager never
        // lists errorData.message as pageable, so the reader must fall back to it instead of answering "stored whole".
        var lines = Enumerable.Range(0, 600).Select(i => $"batchEdits[{i}] (File.cs): failure text describing the rejected edit in detail");
        var message = string.Join("\n", lines);
        var resultId = Guid.NewGuid().ToString("N");
        WriteLargeResultFile(new { isError = true, errorData = new { errorCode = "InvalidArgument", message } }, ResultWrapperType.Envelope, resultId);

        var first = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId);

        Assert.That(first.IsError, Is.False, first.ErrorData?.Message);
        var unit = (string)first.SuccessData!.GetType().GetProperty("Unit")!.GetValue(first.SuccessData)!;
        Assert.That(unit, Is.EqualTo("lines"));
        var total = (int)first.SuccessData!.GetType().GetProperty("Total")!.GetValue(first.SuccessData)!;
        Assert.That(total, Is.EqualTo(600), "Total must be the line count of errorData.message.");
        var returned = (int)first.SuccessData!.GetType().GetProperty("Returned")!.GetValue(first.SuccessData)!;
        Assert.That(returned, Is.GreaterThan(0));
        Assert.That(first.HasMoreData, Is.True);
        Assert.That(first.NextOffset, Is.EqualTo(returned));

        var second = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, offset: first.NextOffset!.Value);

        Assert.That(second.IsError, Is.False, second.ErrorData?.Message);
        var secondOffset = (int)second.SuccessData!.GetType().GetProperty("Offset")!.GetValue(second.SuccessData)!;
        Assert.That(secondOffset, Is.EqualTo(first.NextOffset));
        var secondReturned = (int)second.SuccessData!.GetType().GetProperty("Returned")!.GetValue(second.SuccessData)!;
        Assert.That(secondReturned, Is.GreaterThan(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T21 - A stored file written before the result marshaller (Raw, or a typed list wrapper whose Type name was deleted)
    //       cannot be paged: the caller gets one structured, actionable error, never the JsonException text.
    // ══════════════════════════════════════════════════════════════════════════

    [TestCase("Raw")]
    [TestCase("TextSearchMatchList")]
    [TestCase("MigrationCandidateFindingList")]
    [Test, CancelAfter(10000)]
    public async Task T21_GetLargeResult_FileFromOlderServer_ReturnsStructuredOlderVersionError(string oldTypeName)
    {
        var resultId = Guid.NewGuid().ToString("N");
        // Written by hand: the Type names no longer exist in ResultWrapperType, so the test cannot build the wrapper from the enum.
        File.WriteAllText(LargeResultsFilePath(resultId), $"{{\"Type\":\"{oldTypeName}\",\"Data\":{{\"text\":\"old payload\"}}}}", new UTF8Encoding(false));

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Is.EqualTo(
            "This stored result was written by an older server version and cannot be paged; repeat the original call."));
        Assert.That(result.ErrorData!.Detail, Is.Null.Or.Empty, "No raw exception text may reach the caller.");
        Assert.That(result.ErrorData!.Message, Does.Not.Contain(oldTypeName).And.Not.Contain("JSON").And.Not.Contain(_tempDir));
    }
}
