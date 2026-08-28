using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for SentinelWorkspaceTools.GetLargeResult:
///   T1  – No resultId and no filePath → error "Result file not found"
///   T2  – Unknown resultId (file doesn't exist) → error
///   T3  – Valid resultId, MigrationCandidateFindingList file → findings returned, TotalRecords set
///   T4  – Valid resultId, ApiSurfaceEntryList file → entries returned
///   T5  – FilePath inside largeresults directory → findings returned
///   T6  – FilePath outside largeresults directory → error
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

        var config = new SentinelConfiguration();
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);

        _workspaceTools = new SentinelWorkspaceTools(_workspaceManager, new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()), new DiffEngine(), new DiagnosticEngine(_workspaceManager), new SolutionManagementEngine(_workspaceManager), new StructuralRefinementEngine(_workspaceManager, config), new DependencyEngine(_workspaceManager), new ProjectConsistencyEngine(_workspaceManager), config, NullLogger<SentinelWorkspaceTools>.Instance, new BuildEngine(_workspaceManager, new DiagnosticEngine(_workspaceManager)));
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

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – No resultId and no filePath → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_GetLargeResult_NoResultIdNoFilePath_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResult();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("Result file not found").Or.Contain("resultId").Or.Contain("filePath"),
            "Error should explain that a resultId or filePath is required.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – Unknown resultId → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T2_GetLargeResult_UnknownResultId_ReturnsError()
    {
        var result = await _workspaceTools.GetLargeResult(resultId: "00000000000000000000000000000000");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T3 – Valid resultId, MigrationCandidateFindingList → findings returned, TotalRecords set
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_GetLargeResult_ValidScanId_MigrationCandidates_ReturnsFindingsAndTotalRecords()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(5);
        WriteLargeResultFile(findings, ResultWrapperType.MigrationCandidateFindingList, resultId);

        var result = await _workspaceTools.GetLargeResult(resultId: resultId, limit: 3, offset: 0);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5), "TotalRecords must match the item count in the file.");
        Assert.That(result.HasMorePages, Is.True, "limit=3 of 5 total → HasMorePages should be true.");

        // GetLargeResult's Data is the flat, paged List<MigrationCandidateFinding> — the same
        // shape every other ToolResult<object>-returning tool uses; it used to be double-wrapped
        // in an inner ToolResult<object>, which was a bug (fixed alongside these assertions).
        var returnedFindings = result.Data as List<MigrationCandidateFinding>;
        Assert.That(returnedFindings, Is.Not.Null, "Data should be List<MigrationCandidateFinding>.");
        Assert.That(returnedFindings!.Any(f => f.MethodName == "loadList_0"), Is.True,
            "loadList_0 should be present in the returned findings.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – Valid resultId, ApiSurfaceEntryList → entries returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_GetLargeResult_ValidScanId_ApiSurfaceEntryList_ReturnsEntries()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var entries = MakeApiSurfaceEntries(4);
        WriteLargeResultFile(entries, ResultWrapperType.ApiSurfaceEntryList, resultId);

        var result = await _workspaceTools.GetLargeResult(resultId: resultId, limit: 10, offset: 0);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(4));
        Assert.That(result.HasMorePages, Is.False, "limit=10 of 4 total → HasMorePages should be false.");

        var returnedEntries = result.Data as List<ApiSurfaceEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "Data should be List<ApiSurfaceEntry>.");
        Assert.That(returnedEntries!.Count, Is.EqualTo(4));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – FilePath inside scans directory → findings returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T5_GetLargeResult_ValidFilePath_InLargeResultsDir_ReturnsFindings()
    {
        var resultId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(2);
        var filePath = WriteLargeResultFile(findings, ResultWrapperType.MigrationCandidateFindingList, resultId);

        var result = await _workspaceTools.GetLargeResult(filepath: filePath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(2));
        Assert.That(result.Data, Is.InstanceOf<List<MigrationCandidateFinding>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – FilePath outside scans directory → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T6_GetLargeResult_FilePathOutsideScansDir_ReturnsError()
    {
        // Write a file that looks like a result file but is outside the largeresults directory.
        var outsidePath = Path.Combine(_tempDir, "result_20260101T000000Z_fakeid.json");
        await File.WriteAllTextAsync(outsidePath, "{}");

        var result = await _workspaceTools.GetLargeResult(filepath: outsidePath);

        Assert.That(result.Success, Is.False,
            "A result file outside .roslynsentinel/largeresults/ must be rejected.");
        Assert.That(result.Error, Is.Not.Null);
    }
}
