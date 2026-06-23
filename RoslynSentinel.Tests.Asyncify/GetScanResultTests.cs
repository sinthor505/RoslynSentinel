using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for SentinelScanTools.GetScanResult:
///   T1  – No scanId and no filePath → error "Scan file not found"
///   T2  – Unknown scanId (file doesn't exist) → error
///   T3  – Valid scanId, MigrationCandidateFindingList file → findings returned, TotalRecords set
///   T4  – Valid scanId, ApiSurfaceEntryList file → entries returned
///   T5  – FilePath inside scans directory → findings returned
///   T6  – FilePath outside scans directory → error
/// </summary>
[TestFixture]
public class GetScanResultTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelScanTools _scanTools;
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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "GetScanResultTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var config = new SentinelConfiguration();
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);

        _scanTools = new SentinelScanTools(
            new AnalysisEngine(_workspaceManager, config),
            new SecurityEngine(_workspaceManager),
            new AntiPatternEngine(_workspaceManager),
            new AsyncSafetyEngine(_workspaceManager),
            new ThreadSafetyEngine(_workspaceManager),
            new ControlFlowEngine(_workspaceManager),
            new PerformanceEngine(_workspaceManager),
            new DeadCodeEngine(_workspaceManager),
            new DependencyEngine(_workspaceManager),
            new ArchitecturalEngine(_workspaceManager),
            new ProjectStructureEngine(_workspaceManager, config),
            new DependencyInjectionEngine(_workspaceManager),
            new ProjectConsistencyEngine(_workspaceManager),
            new MetricsEngine(_workspaceManager),
            new CloneDetectionEngine(_workspaceManager),
            new DiscoveryEngine(_workspaceManager, symbolNavEngine),
            new StackOverflowEngine(_workspaceManager),
            new CodeStyleEngine(_workspaceManager, config),
            new CodeStyleAnalysisEngine(_workspaceManager),
            new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config),
            symbolNavEngine,
            new BreakingChangeEngine(_workspaceManager),
            _workspaceManager,
            NullLogger<SentinelScanTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteScanFile<T>(T data, ScanWrapperType type, string scanId)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "scans");
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var filePath = Path.Combine(dir, $"scan_{ts}_{scanId}.json");
        var wrapper = new ScanWapper
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
    // T1 – No scanId and no filePath → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_GetScanResult_NoScanIdNoFilePath_ReturnsError()
    {
        var result = await _scanTools.GetScanResult();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("Scan file not found").Or.Contain("scanId").Or.Contain("filePath"),
            "Error should explain that a scanId or filePath is required.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – Unknown scanId → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T2_GetScanResult_UnknownScanId_ReturnsError()
    {
        var result = await _scanTools.GetScanResult(scanId: "00000000000000000000000000000000");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T3 – Valid scanId, MigrationCandidateFindingList → findings returned, TotalRecords set
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_GetScanResult_ValidScanId_MigrationCandidates_ReturnsFindingsAndTotalRecords()
    {
        var scanId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(5);
        WriteScanFile(findings, ScanWrapperType.MigrationCandidateFindingList, scanId);

        var result = await _scanTools.GetScanResult(scanId: scanId, limit: 3, offset: 0);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(5), "TotalRecords must match the item count in the file.");
        Assert.That(result.HasMore, Is.True, "limit=3 of 5 total → HasMore should be true.");

        var inner = result.Data as ToolResult<object>;
        Assert.That(inner, Is.Not.Null, "Data should be an inner ToolResult<object>.");
        Assert.That(inner!.Success, Is.True);
        var returnedFindings = inner.Data as List<MigrationCandidateFinding>;
        Assert.That(returnedFindings, Is.Not.Null, "Inner Data should be List<MigrationCandidateFinding>.");
        Assert.That(returnedFindings!.Any(f => f.MethodName == "loadList_0"), Is.True,
            "loadList_0 should be present in the returned findings.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – Valid scanId, ApiSurfaceEntryList → entries returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_GetScanResult_ValidScanId_ApiSurfaceEntryList_ReturnsEntries()
    {
        var scanId = Guid.NewGuid().ToString("N");
        var entries = MakeApiSurfaceEntries(4);
        WriteScanFile(entries, ScanWrapperType.ApiSurfaceEntryList, scanId);

        var result = await _scanTools.GetScanResult(scanId: scanId, limit: 10, offset: 0);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(4));
        Assert.That(result.HasMore, Is.False, "limit=10 of 4 total → HasMore should be false.");

        var inner = result.Data as ToolResult<object>;
        Assert.That(inner, Is.Not.Null);
        var returnedEntries = inner!.Data as List<ApiSurfaceEntry>;
        Assert.That(returnedEntries, Is.Not.Null, "Inner Data should be List<ApiSurfaceEntry>.");
        Assert.That(returnedEntries!.Count, Is.EqualTo(4));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – FilePath inside scans directory → findings returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T5_GetScanResult_ValidFilePath_InScansDir_ReturnsFindigns()
    {
        var scanId = Guid.NewGuid().ToString("N");
        var findings = MakeMigrationFindings(2);
        var filePath = WriteScanFile(findings, ScanWrapperType.MigrationCandidateFindingList, scanId);

        var result = await _scanTools.GetScanResult(filepath: filePath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.TotalRecords, Is.EqualTo(2));
        var inner = result.Data as ToolResult<object>;
        Assert.That(inner, Is.Not.Null);
        Assert.That(inner!.Data, Is.InstanceOf<List<MigrationCandidateFinding>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – FilePath outside scans directory → error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T6_GetScanResult_FilePathOutsideScansDir_ReturnsError()
    {
        // Write a file that looks like a scan file but is outside the scans directory.
        var outsidePath = Path.Combine(_tempDir, "scan_20260101T000000Z_fakeid.json");
        await File.WriteAllTextAsync(outsidePath, "{}");

        var result = await _scanTools.GetScanResult(filepath: outsidePath);

        Assert.That(result.Success, Is.False,
            "A scan file outside .roslynsentinel/scans/ must be rejected.");
        Assert.That(result.Error, Is.Not.Null);
    }
}
