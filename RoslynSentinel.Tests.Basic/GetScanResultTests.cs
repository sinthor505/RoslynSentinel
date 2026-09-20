using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for SentinelWorkspaceTools.GetLargeResult:
///   T1  – No resultId and no filePath -> error "Result file not found"
///   T2  – Unknown resultId (file doesn't exist) -> error
///   T3  – Valid resultId, MigrationCandidateFindingList file -> findings returned, TotalRecords set
///   T4  – Valid resultId, ApiSurfaceEntryList file -> entries returned
///   T5  – FilePathWrapper inside largeresults directory -> findings returned
///   T6  – FilePathWrapper outside largeresults directory -> error
/// </summary>
[TestFixture]
public class GetLargeResultTests
{
    private IWorkspaceManager _workspaceManager;
    //private SentinelScanTools _scanTools;
    private SentinelWorkspaceTools _workspaceTools;
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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "GetLargeResultTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", []));

        var config = new SentinelConfiguration();
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);

        _workspaceTools = new SentinelWorkspaceTools(_workspaceManager, new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()), new DiffEngine(), new DiagnosticEngine(_workspaceManager), new SolutionManagementEngine(_workspaceManager), new StructuralRefinementEngine(_workspaceManager, config), new DependencyEngine(_workspaceManager), new ProjectConsistencyEngine(_workspaceManager), config, NullLogger<SentinelWorkspaceTools>.Instance, new BuildEngine(_workspaceManager, new DiagnosticEngine(_workspaceManager)), symbolNavEngine, new TestRunEngine(_workspaceManager), new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
        var result = await _workspaceTools.GetLargeResult(reason: "test message");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails, Is.Not.Null);
        Assert.That(result.ErrorDetails!.Message, Does.Contain("Result file not found").Or.Contain("resultId").Or.Contain("filePath"),
            "ErrorDetails should explain that a resultId or filePath is required.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – Unknown resultId -> error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T2_GetLargeResult_UnknownResultId_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: "00000000000000000000000000000000");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails, Is.Not.Null);
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

        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: resultId, limit: 3, offset: 0);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5), "TotalRecords must match the item count in the file.");
        Assert.That(result.HasMorePages, Is.True, "limit=3 of 5 total -> HasMorePages should be true.");

        // GetLargeResult's SuccessDetails is the flat, paged List<MigrationCandidateFinding> -> the same
        // shape every other SentinelCallToolResult<object>-returning tool uses; it used to be double-wrapped
        // in an inner SentinelCallToolResult<object>, which was a bug (fixed alongside these assertions).
        var returnedFindings = result.SuccessDetails as List<MigrationCandidateFinding>;
        Assert.That(returnedFindings, Is.Not.Null, "SuccessDetails should be List<MigrationCandidateFinding>.");
        Assert.That(returnedFindings!.Any(f => f.MethodName == "loadList_0"), Is.True,
            "loadList_0 should be present in the returned findings.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – Valid resultId, ApiSurfaceEntryList -> entries returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_GetLargeResult_ValidScanId_ApiSurfaceEntryList_ReturnsEntries()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeApiSurfaceEntries(4);
        WriteLargeResultFile(entries, ResultWrapperType.ApiSurfaceEntryList, resultId);

        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: resultId, limit: 10, offset: 0);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(4));
        Assert.That(result.HasMorePages, Is.False, "limit=10 of 4 total -> HasMorePages should be false.");

        var returnedEntries = result.SuccessDetails as List<ApiSurfaceEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "SuccessDetails should be List<ApiSurfaceEntry>.");
        Assert.That(returnedEntries!.Count, Is.EqualTo(4));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T7 – Valid resultId, SolutionSymbolEntryList (ListAll's offload type) -> entries returned,
    //      and a non-zero offset actually skips records rather than always returning page 1.
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T7_GetLargeResult_ValidScanId_SolutionSymbolEntryList_ReturnsEntries()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeSolutionSymbolEntries(5);
        WriteLargeResultFile(entries, ResultWrapperType.SolutionSymbolEntryList, resultId);

        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: resultId, limit: 3, offset: 0);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5));
        Assert.That(result.HasMorePages, Is.True, "limit=3 of 5 total -> HasMorePages should be true.");

        var returnedEntries = result.SuccessDetails as List<SolutionSymbolEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "SuccessDetails should be List<SolutionSymbolEntry> - ListAll's offloaded results must be pageable, not fall through to \"Unknown scan result type\".");
        Assert.That(returnedEntries!.Select(e => e.Name), Is.EqualTo(new[] { "Method_0", "Method_1", "Method_2" }));
    }

    [Test, CancelAfter(10000)]
    public async Task T8_GetLargeResult_NonZeroOffset_SkipsAlreadySeenRecords()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeSolutionSymbolEntries(5);
        WriteLargeResultFile(entries, ResultWrapperType.SolutionSymbolEntryList, resultId);

        var page1 = await _workspaceTools.GetLargeResult(reason: "test message", resultId: resultId, limit: 2, offset: 0);
        var page2 = await _workspaceTools.GetLargeResult(reason: "test message", resultId: resultId, limit: 2, offset: 2);

        var names1 = ((List<SolutionSymbolEntry>)page1.SuccessDetails!).Select(e => e.Name).ToList();
        var names2 = ((List<SolutionSymbolEntry>)page2.SuccessDetails!).Select(e => e.Name).ToList();

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

        var result = await _workspaceTools.GetLargeResult(reason: "test message", filepath: filePath);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(2));
        Assert.That(result.SuccessDetails, Is.InstanceOf<List<MigrationCandidateFinding>>());
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

        var result = await _workspaceTools.GetLargeResult(reason: "test message", filepath: outsidePath);

        Assert.That(result.IsSuccess, Is.False,
            "A result file outside .roslynsentinel/largeresults/ must be rejected.");
        Assert.That(result.ErrorDetails, Is.Not.Null);
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

    // ═══════════════════════════════════════════════════════════════════════════
    // T9-T12 – ResultWrapperType.Raw (the generic MCP request-filter offload backstop,
    //          see docs/current/proposal_centralized_large_result_filter.md). Unlike every
    //          other case above, Raw has no known element shape, so GetLargeResult pages over
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
        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: stored.resultId, charLimit: LargeResultHelper.OffloadThresholdBytes);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.HasMorePages, Is.False, "The whole stored text fits in one window, so there should be no more pages.");
        Assert.That(result.WarningDetails, Is.Null);

        var text = (string)result.SuccessDetails!.GetType().GetProperty("text")!.GetValue(result.SuccessDetails)!;
        using var roundTripped = JsonDocument.Parse(text);
        Assert.That(roundTripped.RootElement.GetProperty("message").GetString(), Is.EqualTo(payload.message),
            "The Raw case must replay the stored JSON text verbatim, not re-shape it.");
        Assert.That(roundTripped.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(payload.count));
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

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
            var page = await _workspaceTools.GetLargeResult(reason: "test message", resultId: stored.resultId, offset: offset.Value, charLimit: LargeResultHelper.OffloadThresholdBytes);
            Assert.That(page.IsSuccess, Is.True);

            var pageDataType = page.SuccessDetails!.GetType();
            var text = (string)pageDataType.GetProperty("text")!.GetValue(page.SuccessDetails)!;
            reassembled.Append(text);

            // A single page must never itself be big enough to re-trigger the offload filter on
            // its way back out -> otherwise GetLargeResult(Raw) could re-offload under a new
            // resultId in an unbounded fetch/still-too-big/re-offload loop.
            Assert.That(text.Length, Is.LessThanOrEqualTo(LargeResultHelper.OffloadThresholdBytes));

            offset = (int?)pageDataType.GetProperty("nextOffset")!.GetValue(page.SuccessDetails);
            pageCount++;
            Assert.That(pageCount, Is.LessThan(10), "Paging should terminate quickly; more pages than this indicates a broken offset/hasMore computation.");
        }

        Assert.That(pageCount, Is.GreaterThan(1), "A payload larger than one window must take more than one page to read back.");

        // The stored wrapper file is written with WriteIndented=true (LargeResultHelper.JsonOptions),
        // which reformats the nested SuccessDetails payload's whitespace even though StoreRawJsonAsync never
        // re-shapes its structure - so compare parsed JSON, not raw text bytes.
        using var reassembledDoc = JsonDocument.Parse(reassembled.ToString());
        Assert.That(reassembledDoc.RootElement.GetProperty("data").GetString(), Is.EqualTo(original),
            "Concatenating every page and re-parsing must reproduce the original stored JSON data exactly (structural round-trip).");
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

    [Test, CancelAfter(10000)]
    public async Task T11_GetLargeResult_Raw_NonZeroOffset_ClampsToTextLengthInsteadOfThrowing()
    {
        var original = "short text";
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            JsonSerializer.Serialize(original), _tempDir, CancellationToken.None);

        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: stored.resultId, offset: 100_000);

        Assert.That(result.IsSuccess, Is.True, "An offset far past the end of the stored text must clamp, not throw a Substring range exception.");
        var text = (string)result.SuccessDetails!.GetType().GetProperty("text")!.GetValue(result.SuccessDetails)!;
        Assert.That(text, Is.Empty);
        Assert.That(result.HasMorePages, Is.False);
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)

    [Test, CancelAfter(10000)]
    public async Task T12_GetLargeResult_Raw_CharLimitSmallerThanThreshold_UsesCharLimitAsWindowSize()
    {
        var original = new string('y', 200);
        var stored = await LargeResultHelper.StoreRawJsonAsync(
            JsonSerializer.Serialize(original), _tempDir, CancellationToken.None);

        var result = await _workspaceTools.GetLargeResult(reason: "test message", resultId: stored.resultId, offset: 0, charLimit: 50);

        Assert.That(result.IsSuccess, Is.True);
        var text = (string)result.SuccessDetails!.GetType().GetProperty("text")!.GetValue(result.SuccessDetails)!;
        Assert.That(text.Length, Is.EqualTo(50), "A charLimit smaller than OffloadThresholdBytes should be honored as the window size, not ignored.");
        Assert.That(result.HasMorePages, Is.True);
    }
}
