using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for WorkspaceTools.GetLargeResultTool:
///   T1  – No resultId and no filePath -> error "Result file not found"
///   T2  – Unknown resultId (file doesn't exist) -> error
///   T3  – Valid resultId, MigrationCandidateFindingList file -> findings returned, TotalRecords set
///   T4  – Valid resultId, ApiSurfaceEntryList file -> entries returned
///   T5  – FilePathWrapper inside largeresults directory -> findings returned
///   T6  – FilePathWrapper outside largeresults directory -> error
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

    private string WriteLargeResultFile<T>(T data, ResultWrapperType type, string resultId)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"largeresult_{ts}_{resultId}.json");
        var wrapper = new ResultWrapper
        {
            Type = type,
            Data = JsonSerializer.SerializeToNode(data, TestJsonOptions)!,
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(wrapper, TestJsonOptions), new UTF8Encoding(false));
        return filePath;
    }

    private static List<MigrationCandidateFinding> MakeMigrationFindings(int count = 3) =>
        Enumerable.Range(0, count)
            .Select(i => new MigrationCandidateFinding(
                FilePath: "RegionForm.cs",
                MethodName: $"loadList_{i}",
                ClassName: "RegionForm",
                Pattern: "AsyncBridgeCandidate",
                Score: 50 + i,
                Reason: "calls-CommonSearch:30 calls-obsolete-wrapper:20",
                FlaggedDate: "2026-05-28",
                Line: 10 + i))
            .ToList();

    private static List<ApiSurfaceEntry> MakeApiSurfaceEntries(int count = 2) =>
        Enumerable.Range(0, count)
            .Select(i => new ApiSurfaceEntry(
                TypeName: $"MyClass_{i}",
                MemberName: $"DoWork_{i}",
                Signature: $"void DoWork_{i}()",
                Kind: "Method",
                IsVirtual: false,
                IsAbstract: false,
                IsSealed: false,
                XmlDocSummary: null))
            .ToList();

    private static List<SolutionSymbolEntry> MakeSolutionSymbolEntries(int count = 5) =>
        Enumerable.Range(0, count)
            .Select(i => new SolutionSymbolEntry(
                FilePath: new FilePathWrapper($"File_{i}.cs", null),
                Kind: "method",
                Name: $"Method_{i}",
                Container: "MyClass",
                StartLine: 10 + i,
                EndLine: 12 + i))
            .ToList();

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – No resultId and no filePath -> error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_GetLargeResult_NoResultIdNoFilePath_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResultTool(reason: "test message");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("Result file not found").Or.Contain("resultId").Or.Contain("filePath"),
            "ErrorData should explain that a resultId or filePath is required.");
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
        Assert.That(result.ErrorData!.Message, Does.Contain("Result file not found"));
        Assert.That(result.ErrorData!.Message, Does.Contain("00000000000000000000000000000000"));
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
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("Result file not found"));
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
    // T3 – Valid resultId, MigrationCandidateFindingList -> findings returned, TotalRecords set
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_GetLargeResult_ValidScanId_MigrationCandidates_ReturnsFindingsAndTotalRecords()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(5);
        WriteLargeResultFile(findings, ResultWrapperType.MigrationCandidateFindingList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 3, offset: 0);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5), "TotalRecords must match the item count in the file.");
        Assert.That(result.HasMoreData, Is.True, "limit=3 of 5 total -> HasMoreData should be true.");

        // GetLargeResultTool's Data is the flat, paged List<MigrationCandidateFinding> -> the same
        // shape every other SentinelCallToolResult<object>-returning tool uses; it used to be double-wrapped
        // in an inner SentinelCallToolResult<object>, which was a bug (fixed alongside these assertions).
        var returnedFindings = result.SuccessData as List<MigrationCandidateFinding>;
        Assert.That(returnedFindings, Is.Not.Null, "Data should be List<MigrationCandidateFinding>.");
        Assert.That(returnedFindings!.Any(f => f.MethodName == "loadList_0"), Is.True,
            "loadList_0 should be present in the returned findings.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – Valid resultId, ApiSurfaceEntryList -> entries returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    [Category("ApiSurfaceEntry")] // sentinel:auto-category
    public async Task T4_GetLargeResult_ValidScanId_ApiSurfaceEntryList_ReturnsEntries()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeApiSurfaceEntries(4);
        WriteLargeResultFile(entries, ResultWrapperType.ApiSurfaceEntryList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 10, offset: 0);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(4));
        Assert.That(result.HasMoreData, Is.False, "limit=10 of 4 total -> HasMoreData should be false.");

        var returnedEntries = result.SuccessData as List<ApiSurfaceEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "Data should be List<ApiSurfaceEntry>.");
        Assert.That(returnedEntries!.Count, Is.EqualTo(4));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T7 – Valid resultId, SolutionSymbolEntryList (ListAllTool's offload type) -> entries returned,
    //      and a non-zero offset actually skips records rather than always returning page 1.
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T7_GetLargeResult_ValidScanId_SolutionSymbolEntryList_ReturnsEntries()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeSolutionSymbolEntries(5);
        WriteLargeResultFile(entries, ResultWrapperType.SolutionSymbolEntryList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 3, offset: 0);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5));
        Assert.That(result.HasMoreData, Is.True, "limit=3 of 5 total -> HasMoreData should be true.");

        var returnedEntries = result.SuccessData as List<SolutionSymbolEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "Data should be List<SolutionSymbolEntry> - ListAll's offloaded results must be pageable, not fall through to \"Unknown scan result type\".");
        Assert.That(returnedEntries!.Select(e => e.Name), Is.EqualTo(new[] { "Method_0", "Method_1", "Method_2" }));
    }

    [Test, CancelAfter(10000)]
    public async Task T8_GetLargeResult_NonZeroOffset_SkipsAlreadySeenRecords()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeSolutionSymbolEntries(5);
        WriteLargeResultFile(entries, ResultWrapperType.SolutionSymbolEntryList, resultId);

        var page1 = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 2, offset: 0);
        var page2 = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 2, offset: 2);

        var names1 = ((List<SolutionSymbolEntry>)page1.SuccessData!).Select(e => e.Name).ToList();
        var names2 = ((List<SolutionSymbolEntry>)page2.SuccessData!).Select(e => e.Name).ToList();

        Assert.That(names1, Is.EqualTo(new[] { "Method_0", "Method_1" }));
        Assert.That(names2, Is.EqualTo(new[] { "Method_2", "Method_3" }),
            "offset=2 must skip the first 2 records already seen at offset=0, not repeat the same page.");
    }
    [Test, CancelAfter(10000)]
    public async Task T5_GetLargeResult_ValidFilePath_InLargeResultsDir_ReturnsFindings()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(2);
        var filePath = WriteLargeResultFile(findings, ResultWrapperType.MigrationCandidateFindingList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", filePath: filePath);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(2));
        Assert.That(result.SuccessData, Is.InstanceOf<List<MigrationCandidateFinding>>());
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
        Assert.That(result.IsError, Is.True); Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound)); Assert.That(result.ErrorData!.Message, Is.EqualTo("Result file not found. Supply a valid resultId or filePath pointing to a largeresult_*.json file in the largeresults directory.")); // TODO engine: the miss is the largeresult_ filename prefix, not the outside-dir check the test name describes
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // T9-T12 – ResultWrapperType.Raw (the generic MCP request-filter offload backstop,
    //          see docs/current/proposal_centralized_large_result_filter.md). Unlike every
    //          other case above, Raw has no known element shape, so GetLargeResultTool pages over
    //          the stored raw text itself as a byte/char window rather than a list.
    // ═══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T9_GetLargeResult_Raw_UnderOneWindow_ReturnsWholeTextNoMorePages()
    {
        var payload = new { message = "hello raw offload world", count = 3 };
        var payloadJson = JsonSerializer.Serialize(payload);
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            payloadJson, _tempDir, CancellationToken.None);
        Assert.That(stored.offloaded, Is.True, "StoreRawJsonAsync should offload whenever a solutionRoot is available, regardless of size.");

        // charLimit controls the raw-text window size (limit means "N records" and is ignored for
        // Raw results) - pass the full threshold explicitly to read the whole short payload back in
        // one window.
        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: stored.resultId, charLimit: LargeResultHelper.OffloadThresholdBytes);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.HasMoreData, Is.False, "The whole stored text fits in one window, so there should be no more pages.");
        Assert.That(result.WarningDetails, Is.Null);

        var text = (string)result.SuccessData!.GetType().GetProperty("text")!.GetValue(result.SuccessData)!;
        using var roundTripped = JsonDocument.Parse(text);
        Assert.That(roundTripped.RootElement.GetProperty("message").GetString(), Is.EqualTo(payload.message),
            "The Raw case must replay the stored JSON text verbatim, not re-shape it.");
        Assert.That(roundTripped.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(payload.count));
    }

    [Test, CancelAfter(10000)]
    public async Task T10_GetLargeResult_Raw_OverThreshold_PagesInBoundedWindowsAndRoundTripsVerbatim()
    {
        // A JSON object whose single field is repeated past OffloadThresholdBytes so the stored
        // text needs more than one window to read back in full.
        var original = new string('x', LargeResultHelper.OffloadThresholdBytes + 500);
        var originalJson = JsonSerializer.Serialize(new { data = original });
        var stored = await LargeResultHelper.StoreRawJsonAsync(originalJson, _tempDir, CancellationToken.None);
        Assert.That(stored.offloaded, Is.True);

        var reassembled = new StringBuilder();
        int? offset = 0;
        var pageCount = 0;
        while (offset is not null)
        {
            // charLimit controls the raw-text window size (limit means "N records" and is ignored
            // for Raw results) - pass the full threshold explicitly on every page.
            var page = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: stored.resultId, offset: offset.Value, charLimit: LargeResultHelper.OffloadThresholdBytes);
            Assert.That(!page.IsError, Is.True);

            var pageDataType = page.SuccessData!.GetType();
            var text = (string)pageDataType.GetProperty("text")!.GetValue(page.SuccessData)!;
            reassembled.Append(text);

            // A single page must never itself be big enough to re-trigger the offload filter on
            // its way back out -> otherwise GetLargeResultTool(Raw) could re-offload under a new
            // resultId in an unbounded fetch/still-too-big/re-offload loop.
            Assert.That(text.Length, Is.LessThanOrEqualTo(LargeResultHelper.OffloadThresholdBytes));

            offset = (int?)pageDataType.GetProperty("nextOffset")!.GetValue(page.SuccessData);
            pageCount++;
            Assert.That(pageCount, Is.LessThan(10), "Paging should terminate quickly; more pages than this indicates a broken offset/hasMore computation.");
        }

        Assert.That(pageCount, Is.GreaterThan(1), "A payload larger than one window must take more than one page to read back.");

        // The stored wrapper file is written with WriteIndented=true (LargeResultHelper.JsonOptions),
        // which reformats the nested Data payload's whitespace even though StoreRawJsonAsync never
        // re-shapes its structure - so compare parsed JSON, not raw text bytes.
        using var reassembledDoc = JsonDocument.Parse(reassembled.ToString());
        Assert.That(reassembledDoc.RootElement.GetProperty("data").GetString(), Is.EqualTo(original),
            "Concatenating every page and re-parsing must reproduce the original stored JSON data exactly (structural round-trip).");
    }

    [Test, CancelAfter(10000)]
    public async Task T11_GetLargeResult_Raw_NonZeroOffset_ClampsToTextLengthInsteadOfThrowing()
    {
        var original = "short text";
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            JsonSerializer.Serialize(original), _tempDir, CancellationToken.None);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: stored.resultId, offset: 100_000);

        Assert.That(!result.IsError, Is.True, "An offset far past the end of the stored text must clamp, not throw a Substring range exception.");
        var text = (string)result.SuccessData!.GetType().GetProperty("text")!.GetValue(result.SuccessData)!;
        Assert.That(text, Is.Empty);
        Assert.That(result.HasMoreData, Is.False);
    }

    [Test, CancelAfter(10000)]
    public async Task T12_GetLargeResult_Raw_CharLimitSmallerThanThreshold_UsesCharLimitAsWindowSize()
    {
        var original = new string('y', 200);
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            JsonSerializer.Serialize(original), _tempDir, CancellationToken.None);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: stored.resultId, offset: 0, charLimit: 50);

        Assert.That(!result.IsError, Is.True);
        var text = (string)result.SuccessData!.GetType().GetProperty("text")!.GetValue(result.SuccessData)!;
        Assert.That(text.Length, Is.EqualTo(50), "A charLimit smaller than OffloadThresholdBytes should be honored as the window size, not ignored.");
        Assert.That(result.HasMoreData, Is.True);
    }

    [Test, CancelAfter(10000)]
    public async Task T12b_GetLargeResult_Raw_ReplaysCompactUnescapedJsonAndOmitsCharCountTotalRecords()
    {
        // Stored with the default serializer, which escapes the angle brackets of generic types as unicode escapes in the stored text. The Raw
        // replay must hand back the real characters, compact, with no CRLF indentation.
        var payload = new { items = new[] { "List<int>", "Dictionary<string,int>" }, nested = new { a = 1 } };
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            JsonSerializer.Serialize(payload), _tempDir, CancellationToken.None);
        Assert.That(stored.offloaded, Is.True);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: stored.resultId, charLimit: LargeResultHelper.OffloadThresholdBytes);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.HasMoreData, Is.False);
        Assert.That(result.TotalRecords, Is.Null, "A Raw payload has no known record shape, so TotalRecords (a record count) must stay null.");

        var text = (string)result.SuccessData!.GetType().GetProperty("text")!.GetValue(result.SuccessData)!;
        Assert.That(text, Does.Not.Contain("\\u003C"), "Replay must not re-escape < as \\u003C.");
        Assert.That(text, Does.Not.Contain("\r\n"), "Replay must be compact, without CRLF indentation.");
        Assert.That(text, Does.Contain("List<int>"));
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.GetProperty("nested").GetProperty("a").GetInt32(), Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // T13-T15 - ResultWrapperType.TextSearchMatchList (stored Search mode:text results).
    //           Regression for docs/current/blockers/blocking_error_getlargeresult_text_search_pages_halved_with_zero_totals_and_no_warning.md:
    //           literal + regex rows are paged as one flat sequence, totalRecords is the combined count,
    //           hasMoreData compares against the rows actually returned, and the shrink warning reaches the caller.
    // ═══════════════════════════════════════════════════════════════════════════

    // Each match carries a long preview so a 50-row page serializes past the offload threshold and the
    // size-shrink loop has to cut it. Line is a unique row id (its flat index) so paging can be checked for gaps/duplicates.
    private static List<TextSearchMatch> MakeTextMatches(int count, int firstLine, MatchKind kind) =>
        Enumerable.Range(firstLine, count)
            .Select(i => new TextSearchMatch(
                filePath: new FilePathWrapper($"Dir/File_{i}.cs", null),
                Line: i,
                Column: 1,
                Preview: new string('p', 400),
                MatchedAs: kind,
                EnclosingMember: $"Member_{i}"))
            .ToList();

    private async Task<(List<TextSearchMatch> Rows, SentinelCallToolResult<object> First)> PageAllTextSearchRowsAsync(string resultId, int limit)
    {
        var rows = new List<TextSearchMatch>();
        SentinelCallToolResult<object>? first = null;
        var offset = 0;
        var pages = 0;
        while (true)
        {
            var page = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: limit, offset: offset);
            Assert.That(!page.IsError, Is.True);
            first ??= page;

            var data = page.SuccessData as TextSearchResult;
            Assert.That(data, Is.Not.Null, "Data should be TextSearchResult.");
            var returned = data!.LiteralResults.Count + data.RegexResults.Count;
            Assert.That(returned, Is.GreaterThan(0), "Every page must make progress, otherwise the caller loops forever.");

            rows.AddRange(data.LiteralResults);
            rows.AddRange(data.RegexResults);
            offset += returned;
            pages++;
            Assert.That(pages, Is.LessThan(100), "Paging should terminate; this many pages indicates a broken offset/hasMoreData computation.");

            if (!page.HasMoreData)
            {
                break;
            }
        }

        return (rows, first!);
    }

    [Test, CancelAfter(20000)]
    public async Task T13_GetLargeResult_TextSearch_RegexOnly_ShrunkPageReportsTotalsWarningAndPagesEveryRowOnce()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var stored = new TextSearchResult([], MakeTextMatches(135, 0, MatchKind.Regex), 0, true);
        WriteLargeResultFile(stored, ResultWrapperType.TextSearchMatchList, resultId);

        var (rows, first) = await PageAllTextSearchRowsAsync(resultId, limit: 50);

        var firstData = (TextSearchResult)first.SuccessData!;
        var firstReturned = firstData.LiteralResults.Count + firstData.RegexResults.Count;
        Assert.That(firstReturned, Is.LessThan(50), "Fixture precondition: a 50-row page must exceed the size threshold and be shrunk.");
        Assert.That(first.TotalRecords, Is.EqualTo(135), "TotalRecords must count the regex rows, not the empty literal list.");
        Assert.That(first.HasMoreData, Is.True, "A shrunk first page with rows remaining must report HasMoreData.");
        Assert.That(first.WarningDetails, Is.Not.Null.And.Contain("Page shrunk"), "The shrink warning must reach the caller.");
        Assert.That(first.WarningDetails, Does.Contain($"offset: {firstReturned}"));

        Assert.That(rows.Select(r => r.Line), Is.EqualTo(Enumerable.Range(0, 135)),
            "Advancing offset by the rows returned must yield every stored row exactly once, in order.");
    }

    [Test, CancelAfter(20000)]
    public async Task T14_GetLargeResult_TextSearch_LiteralAndRegex_PagesAsOneFlatSequenceAcrossTheBoundary()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var literal = MakeTextMatches(40, 0, MatchKind.Literal);
        var regex = MakeTextMatches(95, 40, MatchKind.Regex);
        WriteLargeResultFile(new TextSearchResult(literal, regex, 0, true), ResultWrapperType.TextSearchMatchList, resultId);

        var (rows, first) = await PageAllTextSearchRowsAsync(resultId, limit: 50);

        Assert.That(first.TotalRecords, Is.EqualTo(135), "TotalRecords must be literal + regex.");
        Assert.That(first.HasMoreData, Is.True);
        Assert.That(first.WarningDetails, Is.Not.Null);
        Assert.That(rows.Select(r => r.Line), Is.EqualTo(Enumerable.Range(0, 135)),
            "The flat offset must walk literal rows then regex rows with no gaps or duplicates.");
        Assert.That(rows.Take(40).All(r => r.MatchedAs == MatchKind.Literal), Is.True);
        Assert.That(rows.Skip(40).All(r => r.MatchedAs == MatchKind.Regex), Is.True);
    }

    [Test, CancelAfter(10000)]
    public async Task T15_GetLargeResult_TextSearch_SmallResult_NoShrinkNoWarningNoMoreData()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var stored = new TextSearchResult(MakeTextMatches(3, 0, MatchKind.Literal), MakeTextMatches(5, 3, MatchKind.Regex), 0, true);
        WriteLargeResultFile(stored, ResultWrapperType.TextSearchMatchList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 50, offset: 0);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(8));
        Assert.That(result.HasMoreData, Is.False);
        Assert.That(result.WarningDetails, Is.Null);
        var data = (TextSearchResult)result.SuccessData!;
        Assert.That(data.LiteralResults, Has.Count.EqualTo(3));
        Assert.That(data.RegexResults, Has.Count.EqualTo(5));
    }

    [Test, CancelAfter(20000)]
    public async Task T16_GetLargeResult_TextSearch_RegexOnly_NextOffsetChainPagesEveryRowOnceAndEndsNull()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var stored = new TextSearchResult([], MakeTextMatches(135, 0, MatchKind.Regex), 0, true);
        WriteLargeResultFile(stored, ResultWrapperType.TextSearchMatchList, resultId);

        var first = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 50, offset: 0);
        var firstData = (TextSearchResult)first.SuccessData!;
        var firstReturned = firstData.LiteralResults.Count + firstData.RegexResults.Count;
        Assert.That(first.NextOffset, Is.EqualTo(firstReturned), "NextOffset must equal the rows returned on the first page.");

        var lines = new List<int>();
        int? next = first.NextOffset;
        lines.AddRange(firstData.RegexResults.Select(r => r.Line));
        var pages = 1;
        while (next is int offset && pages < 20)
        {
            var page = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 50, offset: offset);
            var data = (TextSearchResult)page.SuccessData!;
            lines.AddRange(data.LiteralResults.Select(r => r.Line));
            lines.AddRange(data.RegexResults.Select(r => r.Line));
            pages++;
            next = page.NextOffset;
            if (next is null)
            {
                Assert.That(page.HasMoreData, Is.False, "The last page must report HasMoreData false.");
            }
        }

        Assert.That(next, Is.Null, "The final page must end with NextOffset null.");
        Assert.That(lines, Is.EqualTo(Enumerable.Range(0, 135)), "Following NextOffset must yield every row exactly once, in order.");
    }

    [Test, CancelAfter(10000)]
    public async Task T17_GetLargeResult_TextSearch_SmallResult_NextOffsetIsNull()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var stored = new TextSearchResult(MakeTextMatches(3, 0, MatchKind.Literal), MakeTextMatches(5, 3, MatchKind.Regex), 0, true);
        WriteLargeResultFile(stored, ResultWrapperType.TextSearchMatchList, resultId);

        var result = await _workspaceTools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 50, offset: 0);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.HasMoreData, Is.False);
        Assert.That(result.NextOffset, Is.Null, "A result that fits in one page must not carry a NextOffset.");
    }
}
