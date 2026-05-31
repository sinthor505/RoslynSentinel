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
        var expectedBuckets = new[] { "<0", "0-25", "26-50", "51-75", "76+" };
        foreach (var bucket in expectedBuckets)
        {
            Assert.That(summary.ByScoreBucket.ContainsKey(bucket), Is.True,
                $"Expected bucket key '{bucket}' to be present.");
        }

        // Verify bucket assignments (scores: -5, 10, 30, 60, 80).
        Assert.That(summary.ByScoreBucket["<0"],    Is.EqualTo(1), "Score -5 → bucket '<0'");
        Assert.That(summary.ByScoreBucket["0-25"],  Is.EqualTo(1), "Score 10 → bucket '0-25'");
        Assert.That(summary.ByScoreBucket["26-50"], Is.EqualTo(1), "Score 30 → bucket '26-50'");
        Assert.That(summary.ByScoreBucket["51-75"], Is.EqualTo(1), "Score 60 → bucket '51-75'");
        Assert.That(summary.ByScoreBucket["76+"],   Is.EqualTo(1), "Score 80 → bucket '76+'");

        // By-pattern counts.
        Assert.That(summary.ByPattern["AsyncBridgeCandidate"], Is.EqualTo(3));
        Assert.That(summary.ByPattern["HandlerExtract"],       Is.EqualTo(2));
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
}
