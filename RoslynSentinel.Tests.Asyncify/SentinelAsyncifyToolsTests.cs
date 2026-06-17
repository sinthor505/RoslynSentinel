using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for SentinelAsyncifyTools public MCP methods:
///   T1  – ScanMigrationCandidates, no solution → SolutionNotLoaded
///   T2  – ScanMigrationCandidates, loadList with [MigrationCandidate] → finding returned
///   T3  – ScanMigrationCandidates, summarize=true → MigrationScanSummary with 5 bucket keys
///   T4  – ScanMigrationCandidates, minScore above all scores → empty list
///   T5  – GetAsyncMigrationProgress, no solution → SolutionNotLoaded
///   T6  – GetAsyncMigrationProgress, solution with async methods → report populated
///   T7  – AsyncMigrate, no solution → SolutionNotLoaded
///   T8  – AsyncMigrate, unknown operation → InvalidArgument
///   T9  – AsyncMigrate, flag_migration_candidates, scope=targets, DryRun=true → succeeded
///   T10 – AsyncMigrate, convert_to_async_bridge, DryRun=true → all items skipped
///   T11 – AsyncMigrate, add_cancellation_token, DryRun=true → returns without error
///   T12 – AsyncMigrate, propagate_cancellation_token, empty targets → 0 attempted, success
///   T13 – AsyncMigrate, handler_extract, DryRun=true, missing ContextSnippet → failed items
/// </summary>
[TestFixture]
public class SentinelAsyncifyToolsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelAsyncifyTools _asyncifyTools;
    private string _tempDir;

    private const string AttrStub = """
        internal sealed class MigrationCandidateAttribute : System.Attribute
        {
            public MigrationCandidateAttribute(string p) {}
            public int Score { get; set; }
            public string? Reason { get; set; }
            public string? FlaggedDate { get; set; }
        }
        """;

    // loadList source adapted from Avaal3 RegionForm.cs — WinForms deps stripped.
    private const string LoadListFlaggedSource = """
        public class RegionForm
        {
            private static object regionListBindingSource;

            [MigrationCandidate("AsyncBridge", Score = 50, Reason = "calls-CommonSearch:30 calls-obsolete-wrapper:20", FlaggedDate = "2026-05-28")]
            private void loadList()
            {
                regionListBindingSource = CommonSearch.search(TripRegion.listAllSQL);
            }

            private static class CommonSearch { public static object search(string sql) => sql; }
            private static class TripRegion { public static string listAllSQL = "SELECT * FROM TripRegion"; }
        }
        """ + "\n" + AttrStub;

    // loadList without attribute — for flag_migration_candidates tests.
    private const string LoadListUnflaggedSource = """
        public class RegionForm
        {
            private static object regionListBindingSource;

            private void loadList()
            {
                regionListBindingSource = CommonSearch.search(TripRegion.listAllSQL);
            }

            private static class CommonSearch { public static object search(string sql) => sql; }
            private static class TripRegion { public static string listAllSQL = "SELECT * FROM TripRegion"; }
        }
        """;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "SentinelAsyncifyToolsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var diffEngine = new DiffEngine(_workspaceManager);
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine);
        var antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        var asyncOptEngine = new AsyncOptimizationEngine(_workspaceManager);
        var asyncBatchEngine = new AsyncBatchEngine(
            _workspaceManager,
            asyncOptEngine,
            validationEngine,
            antiPatternEngine,
            NullLogger<AsyncBatchEngine>.Instance);

        _asyncifyTools = new SentinelAsyncifyTools(
            antiPatternEngine,
            new AsyncSafetyEngine(_workspaceManager),
            asyncOptEngine,
            asyncBatchEngine,
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            NullLogger<SentinelAsyncifyTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void SetSource(string source, string fileName = "RegionForm.cs")
    {
        var solution = AsyncifyTestHelper.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – ScanMigrationCandidates, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_ScanMigrationCandidates_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.ScanMigrationCandidates();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – ScanMigrationCandidates, loadList with [MigrationCandidate] → finding returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T2_ScanMigrationCandidates_LoadListWithAttribute_FindsCandidate()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanMigrationCandidates();

        Assert.That(result.Success, Is.True);
        var findings = result.Data as List<MigrationCandidateFinding>;
        Assert.That(findings, Is.Not.Null, "Data should be List<MigrationCandidateFinding> for summarize=false.");
        Assert.That(findings!.Any(f => f.MethodName == "loadList"), Is.True,
            "loadList should be found as a migration candidate.");
        var finding = findings.First(f => f.MethodName == "loadList");
        Assert.That(finding.ClassName, Is.EqualTo("RegionForm"));
        Assert.That(finding.Pattern, Does.Contain("AsyncBridge"));
        Assert.That(finding.Score, Is.EqualTo(50));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T3 – ScanMigrationCandidates, summarize=true → MigrationScanSummary, 5 bucket keys
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_ScanMigrationCandidates_Summarize_ReturnsMigrationScanSummary()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanMigrationCandidates(summarize: true);

        Assert.That(result.Success, Is.True);
        var summary = result.Data as MigrationScanSummary;
        Assert.That(summary, Is.Not.Null, "Data should be MigrationScanSummary for summarize=true.");
        Assert.That(summary!.TotalCandidates, Is.GreaterThanOrEqualTo(1));

        var expectedBuckets = new[] { "<0", "0-25", "26-50", "51-75", "76plus" };
        foreach (var bucket in expectedBuckets)
            Assert.That(summary.ByScoreBucket.ContainsKey(bucket), Is.True, $"Bucket key '{bucket}' must be present.");

        Assert.That(summary.ByClass, Is.Not.Null);
        Assert.That(summary.ByClass.Count, Is.GreaterThanOrEqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – ScanMigrationCandidates, minScore above all → empty list
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_ScanMigrationCandidates_MinScoreAboveAll_ReturnsEmptyList()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanMigrationCandidates(minScore: 100);

        Assert.That(result.Success, Is.True);
        var findings = result.Data as List<MigrationCandidateFinding>;
        Assert.That(findings, Is.Not.Null);
        Assert.That(findings!.Count, Is.EqualTo(0),
            "minScore=100 should filter out loadList which has Score=50.");
        Assert.That(result.TotalRecords, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – GetAsyncMigrationProgress, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T5_GetAsyncMigrationProgress_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.GetAsyncMigrationProgress();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – GetAsyncMigrationProgress, solution with async methods → report populated
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(15000)]
    public async Task T6_GetAsyncMigrationProgress_WithAsyncMethods_ReturnsReport()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Svc
{
    public async Task DoWorkAsync() { await Task.Delay(1); }
    public async Task<int> GetValueAsync(System.Threading.CancellationToken ct) { return await Task.FromResult(1); }
}", "Svc.cs");

        var result = await _asyncifyTools.GetAsyncMigrationProgress();

        Assert.That(result.Success, Is.True);
        Assert.That(result.Error, Is.Null);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.TotalAsyncMethods, Is.GreaterThanOrEqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T7 – AsyncMigrate, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T7_AsyncMigrate_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.AsyncMigrate(
            "propagate_cancellation_token",
            new AsyncMigrateInput(),
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T8 – AsyncMigrate, unknown operation → InvalidArgument
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T8_AsyncMigrate_UnknownOperation_ReturnsInvalidArgument()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.AsyncMigrate(
            "totally_unknown_operation",
            new AsyncMigrateInput(),
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.InvalidArgument));
        Assert.That(result.Error.Message, Does.Contain("propagate_cancellation_token").Or.Contain("asyncify"),
            "Error message should list valid operation names.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T9 – AsyncMigrate, flag_migration_candidates, scope=targets, DryRun=true → succeeded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T9_AsyncMigrate_FlagMigrationCandidates_DryRunTargets_Succeeds()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.AsyncMigrate(
            "flag_migration_candidates",
            new AsyncMigrateInput
            {
                FlagScope = "targets",
                FlagTargets =
                [
                    new FlagCandidateTarget
                    {
                        FilePath = "RegionForm.cs",
                        MethodName = "loadList",
                        Pattern = "AsyncBridgeCandidate",
                        Score = 50,
                        Reason = "calls-CommonSearch:30 calls-obsolete-wrapper:20",
                    }
                ],
                DryRun = true,
            },
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BreakerOpen, Is.False);
        Assert.That(result.Data.Succeeded, Is.EqualTo(1),
            "loadList should be flagged (DryRun=true — changes computed but not written).");
        Assert.That(result.Data.Failed, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T10 – AsyncMigrate, convert_to_async_bridge, DryRun=true → all items skipped
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T10_AsyncMigrate_ConvertToAsyncBridge_DryRun_AllItemsSkipped()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.AsyncMigrate(
            "convert_to_async_bridge",
            new AsyncMigrateInput
            {
                BatchTargets =
                [
                    new BatchTarget { FilePath = "RegionForm.cs", MethodNames = ["loadList"] }
                ],
                DryRun = true,
            },
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        // DryRun=true short-circuits each method as Skipped but counts toward Succeeded.
        Assert.That(result.Data!.Attempted, Is.EqualTo(1));
        Assert.That(result.Data.Failed, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T11 – AsyncMigrate, add_cancellation_token, DryRun=true → returns without error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T11_AsyncMigrate_AddCancellationToken_DryRun_ReturnsWithoutError()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Svc
{
    public async Task DoWorkAsync() { await Task.Delay(1); }
}", "Svc.cs");

        var result = await _asyncifyTools.AsyncMigrate(
            "add_cancellation_token",
            new AsyncMigrateInput
            {
                BatchTargets = [new BatchTarget { FilePath = "Svc.cs" }],
                DryRun = true,
            },
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BreakerOpen, Is.False);
        Assert.That(result.Data.Failures, Is.Empty.Or.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T12 – AsyncMigrate, propagate_cancellation_token, empty targets → 0 attempted, success
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T12_AsyncMigrate_PropagateCancellationToken_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.AsyncMigrate(
            "propagate_cancellation_token",
            new AsyncMigrateInput
            {
                BatchTargets = [],
                DryRun = true,
            },
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Attempted, Is.EqualTo(0));
        Assert.That(result.Data.BreakerOpen, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T13 – AsyncMigrate, handler_extract, DryRun=true, missing ContextSnippet → failed items
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T13_AsyncMigrate_HandlerExtract_MissingContextSnippet_ReturnsFailed()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.AsyncMigrate(
            "handler_extract",
            new AsyncMigrateInput
            {
                HandlerExtractTargets =
                [
                    new HandlerExtractTarget
                    {
                        FilePath = "RegionForm.cs",
                        NewMethodName = "DoSearch",
                        ContextSnippet = "",   // intentionally empty → validation failure
                    }
                ],
                DryRun = true,
            },
            progress: null,
            cancellationToken: default);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Failed, Is.EqualTo(1),
            "An empty ContextSnippet should produce one failed item.");
        Assert.That(result.Data.Failures, Is.Not.Empty);
        Assert.That(result.Data.Failures[0].Reason, Does.Contain("ContextSnippet"));
    }
}
