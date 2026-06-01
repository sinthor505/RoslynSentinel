using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the migration-scan result-handling spec (v2):
///   T1  – summarize=true returns MigrationScanSummary with all 5 bucket keys.
///   T2  – paginated scan (inline, fits) sets TotalRecords / HasMore / Data.
///   T3  – ~9 KB result (~50-60 candidates) stays inline (threshold regression anchor).
///   T4  – genuinely large result triggers file write (LargeResult populated, Data null).
///   T5  – get_scan_result reads T4's file, paging works, TotalRecords matches.
///   T6  – full absolute filePath gives the same records as filename-only input.
///   T7  – filePath matching nothing → Success=false, ErrorCode="InvalidArgument".
///   T8  – get_async_migration_progress, no solution → ErrorCode="SolutionNotLoaded".
///   T9  – get_async_migration_progress, forced exception → ErrorCode="Exception", Detail non-empty.
/// </summary>
[TestFixture]
public class MigrationScanResultTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine   _asyncOptimizationEngine;
    private AntiPatternEngine          _antiPatternEngine;
    private SentinelQualityTools       _qualityTools;
    private string                     _tempDir;

    // ── attribute stub included in every source snippet ──────────────────────
    private const string AttrStub = """
        internal sealed class MigrationCandidateAttribute : System.Attribute
        {
            public MigrationCandidateAttribute(string p) {}
            public int Score { get; set; }
            public string? Reason { get; set; }
            public string? FlaggedDate { get; set; }
        }
        """;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager       = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _antiPatternEngine       = new AntiPatternEngine(_workspaceManager);

        _qualityTools = new SentinelQualityTools(
            new PerformanceEngine(_workspaceManager),
            new SecurityEngine(_workspaceManager),
            new TestingEngine(_workspaceManager),
            new ControlFlowEngine(_workspaceManager),
            new LogicOptimizationEngine(_workspaceManager),
            new AnalysisEngine(_workspaceManager, new SentinelConfiguration()),
            new AsyncSafetyEngine(_workspaceManager),
            _antiPatternEngine,
            _asyncOptimizationEngine,
            new ThreadSafetyEngine(_workspaceManager),
            new DiagnosticEngine(_workspaceManager),
            new CodeStyleAnalysisEngine(_workspaceManager),
            new PathDrivenTestEngine(_workspaceManager),
            new StackOverflowEngine(_workspaceManager),
            new AsyncBatchEngine(
                _workspaceManager,
                _asyncOptimizationEngine,
                new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine(_workspaceManager)),
                new AntiPatternEngine(_workspaceManager),
                NullLogger<AsyncBatchEngine>.Instance),
            _workspaceManager,
            NullLogger<SentinelQualityTools>.Instance);

        // Create a temp dir so GetSolutionRoot() returns a valid path for file-write tests.
        _tempDir = Path.Combine(Path.GetTempPath(), "MigrationScanResultTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Point the workspace at a fake solution path inside the temp dir.
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
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

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetSource(string source, string fileName = "Service.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>
    /// Builds a C# source file containing <paramref name="count"/> methods, each decorated
    /// with a <c>[MigrationCandidate]</c> attribute. When <paramref name="reason"/> is
    /// supplied it is written into the Reason named argument to pad the JSON output.
    /// </summary>
    private static string BuildManyFlaggedMethods(int count, string? reason = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("public class Svc {");
        for (int i = 0; i < count; i++)
        {
            if (reason != null)
            {
                sb.AppendLine($@"    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 75, Reason = ""{reason}"", FlaggedDate = ""2026-01-01"")]");
            }
            else
            {
                sb.AppendLine($@"    [MigrationCandidate(""AsyncBridgeCandidate"", FlaggedDate = ""2026-01-01"")]");
            }
            sb.AppendLine($"    public int Method{i}(int x) => x + {i};");
        }
        sb.AppendLine("}");
        sb.AppendLine(AttrStub);
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – summarize=true → MigrationScanSummary, all 5 bucket keys present
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T1_Summarize_ReturnsMigrationScanSummary_AllBucketKeysPresent()
    {
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = -5,  FlaggedDate = ""2026-01-01"")]
    public int MethodA(int x) => x;

    [MigrationCandidate(""HandlerExtract"", Score = 10,  FlaggedDate = ""2026-01-01"")]
    public int MethodB(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 30,  FlaggedDate = ""2026-01-01"")]
    public int MethodC(int x) => x;

    [MigrationCandidate(""HandlerExtract"", Score = 60,  FlaggedDate = ""2026-01-01"")]
    public int MethodD(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 80,  FlaggedDate = ""2026-01-01"")]
    public int MethodE(int x) => x;
}}
{AttrStub}");

        var rawResult = await _qualityTools.ScanMigrationCandidates(summarize: true);

        var result = rawResult as MigrationResult<MigrationScanSummary>;
        Assert.That(result, Is.Not.Null, "Should return MigrationResult<MigrationScanSummary> when summarize=true.");
        Assert.That(result!.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);

        var summary = result.Data!;
        Assert.That(summary.TotalCandidates, Is.EqualTo(5));

        // All 5 score buckets must be present in the dictionary.
        var expectedBuckets = new[] { "<0", "0-25", "26-50", "51-75", "76plus" };
        foreach (var bucket in expectedBuckets)
        {
            Assert.That(summary.ByScoreBucket.ContainsKey(bucket), Is.True,
                $"Expected bucket key '{bucket}' to be present.");
        }

        // Verify bucket assignments (scores: -5, 10, 30, 60, 80).
        Assert.That(summary.ByScoreBucket["<0"],     Is.EqualTo(1), "Score -5 → bucket '<0'");
        Assert.That(summary.ByScoreBucket["0-25"],   Is.EqualTo(1), "Score 10 → bucket '0-25'");
        Assert.That(summary.ByScoreBucket["26-50"],  Is.EqualTo(1), "Score 30 → bucket '26-50'");
        Assert.That(summary.ByScoreBucket["51-75"],  Is.EqualTo(1), "Score 60 → bucket '51-75'");
        Assert.That(summary.ByScoreBucket["76plus"], Is.EqualTo(1), "Score 80 → bucket '76plus'");

        // By-pattern counts.
        Assert.That(summary.ByPattern["AsyncBridgeCandidate"], Is.EqualTo(3));
        Assert.That(summary.ByPattern["HandlerExtract"],       Is.EqualTo(2));

        // ByClass: all 5 methods are in the same class "Svc" in the same file.
        Assert.That(summary.ByClass, Is.Not.Null);
        Assert.That(summary.ByClass.Count, Is.EqualTo(1), "All methods are in the same class.");
        var classEntry = summary.ByClass[0];
        Assert.That(classEntry.ClassName,   Is.EqualTo("Svc"));
        Assert.That(classEntry.ProjectName, Is.Not.Null.And.Not.Empty);
        Assert.That(classEntry.FilePath,    Is.Not.Null.And.Not.Empty);
        Assert.That(classEntry.Count,       Is.EqualTo(5));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – paginated scan (page fits) → Data non-null, TotalRecords set, LargeResult null
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T2_PaginatedScan_SmallResult_Inline_TotalRecordsSet()
    {
        // 10 candidates — request page of 3 starting at offset 2.
        SetSource(BuildManyFlaggedMethods(10));

        var rawResult = await _qualityTools.ScanMigrationCandidates(limit: 3, offset: 2);

        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null,     "Inline Data should be populated.");
        Assert.That(result.LargeResult, Is.Null,  "LargeResult should be null for a small page.");
        Assert.That(result.Data!.Count, Is.EqualTo(3), "Page should contain 3 items.");
        Assert.That(result.TotalRecords, Is.EqualTo(10), "TotalRecords should reflect all candidates.");
        Assert.That(result.HasMore, Is.True, "More pages exist beyond offset 2 + limit 3 = 5 < 10.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T3 – ~9 KB result (~50-60 candidates) → stays inline (threshold regression anchor)
    //
    // If this test FAILS (LargeResult is populated), the threshold is measuring the wrong
    // thing. Fix the measurement, not this assertion.
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(15000)]
    public async Task T3_NineKbResult_StaysInline_LargeResultNull()
    {
        // 55 candidates with the default limit of 50 → page ≈ 50 × ~200 bytes ≈ 10 KB.
        // The server threshold is 256 KB. This must stay inline.
        SetSource(BuildManyFlaggedMethods(55));

        var rawResult = await _qualityTools.ScanMigrationCandidates(); // default limit=50, offset=0

        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        Assert.That(result.LargeResult, Is.Null,
            "A ~9 KB page must NOT trigger the server-side file-write threshold (256 KB). " +
            "If this fails, check that the threshold measures serialized bytes, not item count.");
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Count, Is.EqualTo(50), "Default limit of 50 should be applied.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – result genuinely exceeds threshold → LargeResult.WrittenToFile=true
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task T4_LargeResult_WrittenToFile_DataNull()
    {
        // Build ~500 methods, each with a 300-char Reason string.
        // Per finding JSON ≈ 200 (base) + 300 (Reason field) + ~350 (Summary field) ≈ 850 bytes.
        // 500 × 850 = ~425 KB > 256 KB → must trigger the file-write path.
        var reason = new string('x', 300);
        SetSource(BuildManyFlaggedMethods(500, reason));

        // Use a large limit to capture all findings in one page.
        var rawResult = await _qualityTools.ScanMigrationCandidates(limit: 5000, offset: 0);

        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LargeResult, Is.Not.Null,
            "A page > 256 KB should trigger the file-write path.");
        Assert.That(result.LargeResult!.WrittenToFile, Is.True);
        Assert.That(result.LargeResult.FilePath, Is.Not.Null.And.Not.Empty);
        Assert.That(result.LargeResult.OperationId, Is.Not.Null.And.Not.Empty);
        Assert.That(result.LargeResult.SizeBytes, Is.GreaterThan(256 * 1024));
        Assert.That(result.LargeResult.TotalRecords, Is.GreaterThan(0));
        Assert.That(result.Data, Is.Null, "Data should be null when LargeResult is set.");
        Assert.That(File.Exists(result.LargeResult.FilePath), Is.True,
            "Scan file must exist on disk.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – get_scan_result on T4's file → structured records, paging works, TotalRecords matches
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task T5_GetScanResult_ReadsFile_PagingWorks_TotalRecordsMatchesT4()
    {
        var reason = new string('x', 300);
        SetSource(BuildManyFlaggedMethods(500, reason));

        // Run the scan to produce the spill file (same as T4).
        var scanRaw = await _qualityTools.ScanMigrationCandidates(limit: 5000, offset: 0);
        var scanResult = scanRaw as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(scanResult?.LargeResult, Is.Not.Null, "Precondition: scan must have spilled to file.");

        var operationId = scanResult!.LargeResult!.OperationId;
        var totalFromT4 = scanResult.LargeResult.TotalRecords;

        // ── page 1 (limit=10, offset=0) ───────────────────────────────────────
        var page1Result = await _qualityTools.GetScanResult(changeId: operationId, limit: 10, offset: 0);
        Assert.That(page1Result.Success, Is.True);
        Assert.That(page1Result.Data,    Is.Not.Null);
        Assert.That(page1Result.Data!.Count, Is.EqualTo(10));
        Assert.That(page1Result.TotalRecords, Is.EqualTo(totalFromT4),
            "TotalRecords from get_scan_result must match TotalRecords from the original scan.");
        Assert.That(page1Result.HasMore, Is.True);

        // ── verify structured records (not preview text) ──────────────────────
        var first = page1Result.Data![0];
        Assert.That(first.MethodName, Does.StartWith("Method"),
            "Findings must be deserialized as structured MigrationCandidateFinding records.");
        Assert.That(first.Pattern, Is.EqualTo("AsyncBridgeCandidate"));
        Assert.That(first.Score,   Is.EqualTo(75));

        // ── page 2 (limit=10, offset=10) — must be disjoint from page 1 ──────
        var page2Result = await _qualityTools.GetScanResult(changeId: operationId, limit: 10, offset: 10);
        Assert.That(page2Result.Success, Is.True);
        var page1Names = page1Result.Data!.Select(f => f.MethodName).ToHashSet();
        var page2Names = page2Result.Data!.Select(f => f.MethodName).ToHashSet();
        Assert.That(page1Names.Intersect(page2Names), Is.Empty,
            "Pages must not overlap.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – full absolute filePath → same records as filename-only input
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T6_FullAbsoluteFilePath_SameRecordsAsFilenameSuffix()
    {
        // Give the document an absolute-style path so EndsWith matching is tested.
        const string AbsPath = @"C:\Projects\MyApp\Service.cs";
        var source = $@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 50, FlaggedDate = ""2026-01-01"")]
    public int DoWork(int x) => x;
}}
{AttrStub}";

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", [(AbsPath, source)]);
        _workspaceManager.SetTestSolution(solution);

        // Query using only the filename (suffix match).
        var rawSuffix = await _qualityTools.ScanMigrationCandidates(filePath: "Service.cs");
        var suffixResult = rawSuffix as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(suffixResult?.Success, Is.True, "Suffix-only filePath should succeed.");
        Assert.That(suffixResult!.Data?.Count, Is.EqualTo(1));

        // Query using the full absolute path — should yield the same finding.
        var rawAbs = await _qualityTools.ScanMigrationCandidates(filePath: AbsPath);
        var absResult = rawAbs as MigrationResult<List<MigrationCandidateFinding>>;
        Assert.That(absResult?.Success, Is.True, "Full absolute filePath should succeed.");
        Assert.That(absResult!.Data?.Count, Is.EqualTo(1));

        Assert.That(
            absResult.Data![0].MethodName,
            Is.EqualTo(suffixResult.Data![0].MethodName),
            "Both queries must return the same finding.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T7 – filePath matching nothing → Success=false, ErrorCode="InvalidArgument"
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T7_FilePathMatchesNothing_ReturnsInvalidArgument()
    {
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"")]
    public int DoWork(int x) => x;
}}
{AttrStub}", "RealFile.cs");

        var rawResult = await _qualityTools.ScanMigrationCandidates(filePath: "NonExistent.cs");

        var result = rawResult as MigrationResult<object>;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.InvalidArgument));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T8 – get_async_migration_progress, no solution → ErrorCode="SolutionNotLoaded"
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T8_GetAsyncMigrationProgress_NoSolution_ReturnsSolutionNotLoaded()
    {
        // Intentionally do NOT set a solution.
        var result = await _qualityTools.GetAsyncMigrationProgress();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T9 – get_async_migration_progress, forced exception → ErrorCode="Exception", Detail non-empty
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T9_GetAsyncMigrationProgress_ForcedException_ReturnsException_DetailNonEmpty()
    {
        // Provide a minimal solution so the SolutionNotLoaded guard passes,
        // then pass a pre-cancelled token to force an OperationCanceledException inside the engine.
        SetSource(@"public class Svc { public async System.Threading.Tasks.Task DoWork() {} }");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        var result = await _qualityTools.GetAsyncMigrationProgress(
            cancellationToken: cts.Token);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.Exception));
        Assert.That(result.Error.Detail, Is.Not.Null.And.Not.Empty,
            "Detail must carry the exception message.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T10 – summarize=true, topN=5, minScore=70 → TopCandidates ≤5, score≥70, counts populated
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T10_Summarize_TopN_MinScore_PopulatesTopCandidates()
    {
        // 10 methods with scores spread across the range.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("public class Svc {");
        for (int i = 0; i < 10; i++)
        {
            int score = (i + 1) * 10; // 10, 20, 30, 40, 50, 60, 70, 80, 90, 100
            sb.AppendLine($@"    [MigrationCandidate(""AsyncBridgeCandidate"", Score = {score}, FlaggedDate = ""2026-01-01"")]");
            sb.AppendLine($"    public int Method{i}(int x) => x;");
        }
        sb.AppendLine("}");
        sb.AppendLine(AttrStub);
        SetSource(sb.ToString());

        var rawResult = await _qualityTools.ScanMigrationCandidates(summarize: true, topN: 5, minScore: 70);
        var result = rawResult as MigrationResult<MigrationScanSummary>;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        var summary = result.Data!;

        // Normal summary counts reflect post-minScore filter (B7).
        Assert.That(summary.TotalCandidates, Is.EqualTo(4)); // minScore=70: scores 70,80,90,100
        Assert.That(summary.ByPattern.ContainsKey("AsyncBridgeCandidate"), Is.True);

        // TopCandidates: at most 5 items, all with score >= 70.
        Assert.That(summary.TopCandidates, Is.Not.Null, "TopCandidates must be populated when topN is set.");
        Assert.That(summary.TopCandidates!.Count, Is.LessThanOrEqualTo(5));
        Assert.That(summary.TopCandidates.All(c => c.Score >= 70), Is.True,
            "All TopCandidates must satisfy minScore=70.");
        // Results should be in descending score order.
        var scores = summary.TopCandidates.Select(c => c.Score).ToList();
        Assert.That(scores, Is.EqualTo(scores.OrderByDescending(s => s).ToList()),
            "TopCandidates must be sorted descending by score.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T11 – summarize=true without topN/minScore → TopCandidates == null
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T11_Summarize_NoTopN_TopCandidatesNull()
    {
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 80, FlaggedDate = ""2026-01-01"")]
    public int MethodA(int x) => x;
}}
{AttrStub}");

        var rawResult = await _qualityTools.ScanMigrationCandidates(summarize: true);
        var result = rawResult as MigrationResult<MigrationScanSummary>;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        Assert.That(result.Data!.TopCandidates, Is.Null,
            "TopCandidates must be null when neither topN nor minScore is set.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T13 – get_async_migration_progress after load_solution → no exception, report fields set
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(15000)]
    public async Task T13_GetAsyncMigrationProgress_AfterLoadSolution_NoException()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Svc
{
    public async Task DoWork() { await Task.Delay(1); }
    public async Task<int> GetVal(System.Threading.CancellationToken ct) { return await Task.FromResult(1); }
}");

        var result = await _qualityTools.GetAsyncMigrationProgress();

        Assert.That(result.Success, Is.True, "Should succeed with a loaded solution.");
        Assert.That(result.Error, Is.Null);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.TotalAsyncMethods, Is.GreaterThanOrEqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T14 – get_async_migration_progress(projectName) → scoped report, no exception
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(15000)]
    public async Task T14_GetAsyncMigrationProgress_ProjectNameScoped_NoException()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Svc
{
    public async Task DoWork() { await Task.Delay(1); }
}");

        var result = await _qualityTools.GetAsyncMigrationProgress(projectName: "TestProj");

        Assert.That(result.Success, Is.True, "Scoped project query should succeed.");
        Assert.That(result.Error, Is.Null);
        Assert.That(result.Data, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T15 – project_doc read with no solution loaded (only SolutionPath set) → succeeds
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public void T15_ProjectDoc_Read_NoSolutionLoaded_Succeeds()
    {
        // SolutionPath is set in SetUp (_tempDir/Test.sln). CurrentSolution is null here.
        // Create docs/migration-state.yaml at the solution root.
        var docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "migration-state.yaml"), "state: ready\nphase: 1");

        var docTools = new DocumentationTools(
            _workspaceManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentationTools>.Instance);

        var result = docTools.ProjectDoc("read", "state") as DocReadResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Error, Is.Null,
            "Filesystem read should not return an error when SolutionPath is set but solution is not loaded.");
        Assert.That(result.Found, Is.True, "State file should be found.");
        Assert.That(result.Content, Does.Contain("ready"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T16 – All MigrationCandidateFinding.Reason tokens match [\w\-]+:[0-9\-]+
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T16_ScanMigrationCandidates_AllReasonTokensStructured()
    {
        // Set up methods with structured Reason strings (as produced by the scoring engine).
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 55, Reason = ""blocking-calls:40 service-class:15"", FlaggedDate = ""2026-01-01"")]
    public int MethodA(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 75, Reason = ""blocking-calls:40 calls-CommonSearch:30 virtual-override-penalty:-20"", FlaggedDate = ""2026-01-01"")]
    public int MethodB(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 40, FlaggedDate = ""2026-01-01"")]
    public int MethodC(int x) => x;
}}
{AttrStub}");

        var rawResult = await _qualityTools.ScanMigrationCandidates();
        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);

        var tokenPattern = new System.Text.RegularExpressions.Regex(@"^[\w\-]+:[0-9\-]+$");
        foreach (var finding in result.Data!)
        {
            if (string.IsNullOrWhiteSpace(finding.Reason)) continue;

            var tokens = finding.Reason.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                Assert.That(tokenPattern.IsMatch(token), Is.True,
                    $"Reason token '{token}' in method '{finding.MethodName}' does not match key:integer format. Full reason: '{finding.Reason}'");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T17 – async_migrate with unknown operation → ErrorCode="InvalidArgument"
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T17_AsyncMigrate_UnknownOperation_ReturnsInvalidArgument()
    {
        SetSource("public class C { public void M() {} }");

        var result = await _qualityTools.AsyncMigrate(
            "totally_unknown_operation",
            new AsyncMigrateInput());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.InvalidArgument));
        Assert.That(result.Error.Message, Does.Contain("propagate_cancellation_token").Or.Contain("asyncify"),
            "Error message should list valid operation names.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T18 – async_migrate with no solution loaded → ErrorCode="SolutionNotLoaded"
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T18_AsyncMigrate_NoSolution_ReturnsSolutionNotLoaded()
    {
        // Intentionally do NOT set a solution.
        var result = await _qualityTools.AsyncMigrate(
            "propagate_cancellation_token",
            new AsyncMigrateInput());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T19 – large-result offload message contains get_scan_result and OperationId; no read_file
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task T19_LargeResult_Message_ContainsGetScanResult_AndOperationId()
    {
        // Reuse T4 conditions: 500 methods with padded Reason to exceed 256 KB.
        var reason = new string('x', 300);
        SetSource(BuildManyFlaggedMethods(500, reason));

        var rawResult = await _qualityTools.ScanMigrationCandidates(limit: 5000, offset: 0);
        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;

        Assert.That(result?.LargeResult, Is.Not.Null, "Precondition: scan must have spilled to file.");

        var largeResult = result!.LargeResult!;
        Assert.That(largeResult.Message, Is.Not.Null.And.Not.Empty,
            "LargeResult.Message must be populated for agent guidance.");
        Assert.That(largeResult.Message, Does.Contain("get_scan_result"),
            "Message must name the correct recovery tool.");
        Assert.That(largeResult.Message, Does.Not.Contain("read_file"),
            "Message must not reference the non-existent read_file tool.");
        Assert.That(largeResult.Message, Does.Contain(largeResult.OperationId),
            "Message must embed the OperationId so the agent can copy it directly.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T20 – summarize=true, minScore=80 → TotalCandidates = filtered count
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T20_Summarize_MinScore_TotalCandidatesReflectsFilteredCount()
    {
        // 5 methods: scores 50, 60, 70, 80, 90. minScore=80 → only 80 and 90 qualify (count=2).
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 50, FlaggedDate = ""2026-01-01"")]
    public int MethodA(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 60, FlaggedDate = ""2026-01-01"")]
    public int MethodB(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 70, FlaggedDate = ""2026-01-01"")]
    public int MethodC(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 80, FlaggedDate = ""2026-01-01"")]
    public int MethodD(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 90, FlaggedDate = ""2026-01-01"")]
    public int MethodE(int x) => x;
}}
{AttrStub}");

        var rawResult = await _qualityTools.ScanMigrationCandidates(summarize: true, minScore: 80);
        var result = rawResult as MigrationResult<MigrationScanSummary>;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        var summary = result.Data!;

        // TotalCandidates must equal only the count of candidates with Score >= 80.
        Assert.That(summary.TotalCandidates, Is.EqualTo(2),
            "TotalCandidates must reflect the post-minScore filter, not the full candidate count.");

        // ByPattern counts must sum to TotalCandidates.
        var patternTotal = summary.ByPattern.Values.Sum();
        Assert.That(patternTotal, Is.EqualTo(summary.TotalCandidates),
            "Sum of ByPattern counts must equal TotalCandidates.");

        // Result must be inline (no offload).
        Assert.That(result.LargeResult, Is.Null,
            "summarize=true result must always be inline.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T21 – summarize=false, minScore=85 → all records have Score>=85, TotalRecords=filtered count
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T21_Paged_MinScore_AllRecordsAboveThreshold_TotalRecordsFiltered()
    {
        // 6 methods: scores 55, 60, 80, 85, 90, 95. minScore=85 → 3 qualify (85, 90, 95).
        SetSource($@"
public class Svc
{{
    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 55, FlaggedDate = ""2026-01-01"")]
    public int MethodA(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 60, FlaggedDate = ""2026-01-01"")]
    public int MethodB(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 80, FlaggedDate = ""2026-01-01"")]
    public int MethodC(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 85, FlaggedDate = ""2026-01-01"")]
    public int MethodD(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 90, FlaggedDate = ""2026-01-01"")]
    public int MethodE(int x) => x;

    [MigrationCandidate(""AsyncBridgeCandidate"", Score = 95, FlaggedDate = ""2026-01-01"")]
    public int MethodF(int x) => x;
}}
{AttrStub}");

        var rawResult = await _qualityTools.ScanMigrationCandidates(minScore: 85, limit: 20);
        var result = rawResult as MigrationResult<List<MigrationCandidateFinding>>;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);

        // All returned candidates must satisfy minScore=85.
        Assert.That(result.Data!.All(c => c.Score >= 85), Is.True,
            "All returned candidates must have Score >= 85.");

        // TotalRecords must reflect the filtered count, not the total (6) candidates.
        Assert.That(result.TotalRecords, Is.EqualTo(3),
            "TotalRecords must equal the count of candidates with Score >= 85.");
    }
}
