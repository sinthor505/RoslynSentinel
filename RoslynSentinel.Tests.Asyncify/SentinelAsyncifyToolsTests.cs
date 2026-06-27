using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for SentinelAsyncifyTools public MCP methods:
///   T1  – ScanAsyncMigrationCandidates, no solution → SolutionNotLoaded
///   T2  – ScanAsyncMigrationCandidates, loadList with [MigrationCandidate] → finding returned
///   T3  – ScanAsyncMigrationCandidates, summarize=true → MigrationScanSummary with 5 bucket keys
///   T4  – ScanAsyncMigrationCandidates, minScore above all scores → empty list
///   T5  – GetAsyncMigrationProgress, no solution → SolutionNotLoaded
///   T6  – GetAsyncMigrationProgress, solution with async methods → report populated
///   T7  – FlagAsyncMigrationCandidates, no solution → SolutionNotLoaded
///   T8  – BridgeAsyncMethods, empty targets → 0 attempted, success
///   T9  – FlagAsyncMigrationCandidates, scope=targets, DryRun=true → succeeded
///   T10 – BridgeAsyncMethods, DryRun=true → all items attempted, none failed
///   T11 – AddCancellationToken, DryRun=true → returns without error
///   T12 – PropagateCancellationToken, empty targets → 0 attempted, success
///   T13 – ExtractEventHandlers, DryRun=true, missing ContextSnippet → failed items
///   T14 – UpliftCallers, empty targets → 0 attempted, SuggestedPropagateTargets empty
///   T15 – EventHandlersToAsync, no solution → SolutionNotLoaded
///   T17 – UpliftCallers, qualified static call → qualifier preserved (Service.SyncMethodAsync not SyncMethodAsync)
///   T19 – Asyncify macro, default params, Score=49 candidate → Phase 2 skips it (below aligned threshold of 50)
///   T20 – Asyncify macro, default params, Score=70 candidate with caller → bridges AND uplifts (Phase 2 + 3)
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
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);

        ToolGraph toolGraph = ToolGraph.Empty;
        FailureRouter failureRouter = new FailureRouter(toolGraph);

        _asyncifyTools = new SentinelAsyncifyTools(
            antiPatternEngine,
            asyncOptEngine,
            asyncBatchEngine,
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            validationEngine,
            failureRouter,
            new MigrationLedger(),
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
    // T1 – ScanAsyncMigrationCandidates, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_ScanAsyncMigrationCandidates_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.ScanAsyncMigrationCandidates();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – ScanAsyncMigrationCandidates, loadList with [MigrationCandidate] → finding returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T2_ScanAsyncMigrationCandidates_LoadListWithAttribute_FindsCandidate()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidates();

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
    // T3 – ScanAsyncMigrationCandidates, summarize=true → MigrationScanSummary, 5 bucket keys
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_ScanAsyncMigrationCandidates_Summarize_ReturnsMigrationScanSummary()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidates(summarize: true);

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
    // T4 – ScanAsyncMigrationCandidates, minScore above all → empty list
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_ScanAsyncMigrationCandidates_MinScoreAboveAll_ReturnsEmptyList()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidates(minScore: 100);

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
    // T7 – FlagAsyncMigrationCandidates, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T7_FlagAsyncMigrationCandidates_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.FlagAsyncMigrationCandidates(
            scope: "targets",
            flagTargets: []);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T8 – BridgeAsyncMethods, empty targets → 0 attempted, success
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T8_BridgeAsyncMethods_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.BridgeAsyncMethods(targets: []);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Summary.Attempted, Is.EqualTo(0));
        Assert.That(result.Data.SuggestedUpliftTargets, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T9 – FlagAsyncMigrationCandidates, scope=targets, DryRun=true → succeeded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T9_FlagAsyncMigrationCandidates_DryRunTargets_Succeeds()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.FlagAsyncMigrationCandidates(
            scope: "targets",
            flagTargets:
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
            dryRun: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BreakerOpen, Is.False);
        Assert.That(result.Data.Succeeded, Is.EqualTo(1),
            "loadList should be flagged (DryRun=true — changes computed but not written).");
        Assert.That(result.Data.Failed, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T10 – BridgeAsyncMethods, DryRun=true → all items attempted, none failed
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T10_BridgeAsyncMethods_DryRun_AllItemsAttempted()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.BridgeAsyncMethods(
            targets: [new BatchTarget { FilePath = "RegionForm.cs", MethodNames = ["loadList"] }],
            dryRun: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Summary.Attempted, Is.EqualTo(1));
        Assert.That(result.Data.Summary.Failed, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T11 – AddCancellationToken, DryRun=true → returns without error
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T11_AddCancellationToken_DryRun_ReturnsWithoutError()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Svc
{
    public async Task DoWorkAsync() { await Task.Delay(1); }
}", "Svc.cs");

        var result = await _asyncifyTools.AddCancellationToken(
            targets: [new BatchTarget { FilePath = "Svc.cs" }],
            dryRun: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BreakerOpen, Is.False);
        Assert.That(result.Data.Failures, Is.Empty.Or.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T12 – PropagateCancellationToken, empty targets → 0 attempted, success
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T12_PropagateCancellationToken_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.PropagateCancellationToken(
            targets: [],
            dryRun: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Attempted, Is.EqualTo(0));
        Assert.That(result.Data.BreakerOpen, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T13 – ExtractEventHandlers, DryRun=true, missing ContextSnippet → failed items
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T13_ExtractEventHandlers_MissingContextSnippet_ReturnsFailed()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.ExtractEventHandlers(
            targets:
            [
                new HandlerExtractTarget
                {
                    FilePath = "RegionForm.cs",
                    NewMethodName = "DoSearch",
                    ContextSnippet = "",   // intentionally empty → validation failure
                }
            ],
            dryRun: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Failed, Is.EqualTo(1),
            "An empty ContextSnippet should produce one failed item.");
        Assert.That(result.Data.Failures, Is.Not.Empty);
        Assert.That(result.Data.Failures[0].Reason, Does.Contain("ContextSnippet"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T14 – UpliftCallers, empty targets → 0 attempted, SuggestedPropagateTargets empty
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T14_UpliftCallers_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.UpliftCallers(targets: []);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Summary.Attempted, Is.EqualTo(0));
        Assert.That(result.Data.SuggestedPropagateTargets, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T15 – EventHandlersToAsync, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T15_EventHandlersToAsync_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.EventHandlersToAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T16 – Asyncify with maxIterations/maxRuntimeSeconds params, no solution → SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T16_Asyncify_WithLimitParams_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.Asyncify(maxRuntimeSeconds: 30, maxIterations: 10);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T17 – UpliftCallers, qualified static call → type qualifier preserved
    // Regression: callers using FooType.BridgedMethod(args) were incorrectly
    // rewritten to BridgedMethodAsync(args) (qualifier stripped).
    // ══════════════════════════════════════════════════════════════════════════

    private const string QualifiedStaticCallSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;

public class CallerClass
{
    public void DoWork()
    {
        Service.SyncMethod(""arg"");
    }
}

public static class Service
{
    [Obsolete(""Asyncify-bridge: call SyncMethodAsync instead."", false)]
    public static void SyncMethod(string arg)
    {
        SyncMethodAsync(arg).GetAwaiter().GetResult();
    }

    public static async Task SyncMethodAsync(string arg, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken);
    }
}
";

    private const string ChainedCallSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;
public class CallerClass
{
    public string LoadTrimmed() { return DataHelper.GetData().Trim(); }
}
public static class DataHelper
{
    [Obsolete(""Asyncify-bridge: call GetDataAsync instead."", false)]
    public static string GetData() { return GetDataAsync().GetAwaiter().GetResult(); }
    public static async Task<string> GetDataAsync(CancellationToken cancellationToken = default)
    { await Task.Delay(1, cancellationToken); return string.Empty; }
}
";

    [Test, CancelAfter(30000)]
    public async Task T18_UpliftCallers_ChainedMemberAccess_AwaitIsParenthesised()
    {
        SetSource(ChainedCallSource, "Chained.cs");

        var result = await _asyncifyTools.UpliftCallers(
            targets: [new UpliftTarget { BridgedMethodName = "GetData" }],
            maxCallersPerMethod: 5,
            dryRun: false,
            propagateCancellationTokens: false);

        Assert.That(result.Success, Is.True, result.Error?.ToString());

        var solution = _workspaceManager.CurrentSolution;
        var doc = solution?.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, "Chained.cs", StringComparison.OrdinalIgnoreCase));
        Assert.That(doc, Is.Not.Null);
        var text = (await doc!.GetTextAsync()).ToString();

        // The await must be wrapped in parens so the member access binds to the awaited value:
        //   (await GetDataAsync(cancellationToken)).Trim()
        // not:
        //   await GetDataAsync(cancellationToken).Trim()
        Assert.That(text, Does.Contain("(await "),
            "chained await must be parenthesised: `(await GetDataAsync(...)).Member`.");
        Assert.That(text, Does.Not.Contain("await DataHelper.GetDataAsync(cancellationToken)."),
            "bare `await GetDataAsync(ct).Member` must not appear — parens required.");
        Assert.That(text, Does.Not.Contain("await GetDataAsync(cancellationToken)."),
            "bare `await GetDataAsync(ct).Member` must not appear — parens required.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T19 – Asyncify macro, default params, Score=49 candidate → Phase 2 skips it
    // Verifies the lower boundary: a candidate one point below DefaultScoreThreshold=50
    // is found by Phase 2 but excluded, so Phase 3 (uplift) is never entered.
    // ══════════════════════════════════════════════════════════════════════════

    // Score=49 — one point below the aligned DefaultScoreThreshold=50.
    private const string LoadListLowScoreSource = """
        public class RegionForm
        {
            private static object regionListBindingSource;

            [MigrationCandidate("AsyncBridgeCandidate", Score = 49, Reason = "calls-CommonSearch:30 static:5 service-class:14", FlaggedDate = "2026-05-28")]
            private void loadList()
            {
                regionListBindingSource = CommonSearch.search(TripRegion.listAllSQL);
            }

            private static class CommonSearch { public static object search(string sql) => sql; }
            private static class TripRegion { public static string listAllSQL = "SELECT * FROM TripRegion"; }
        }
        """ + "\n" + AttrStub;

    [Test, CancelAfter(15000)]
    public async Task T19_Asyncify_DefaultParams_Score49BelowThreshold_NoUplift()
    {
        // loadList has Score=49, one below DefaultScoreThreshold=50 — must not be bridged.
        SetSource(LoadListLowScoreSource);

        var result = await _asyncifyTools.Asyncify();

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Data, Is.Not.Null);

        Assert.That(result.Data!.Succeeded, Is.EqualTo(0),
            "loadList (Score=49) is below scoreThreshold=50 and must not be bridged.");

        Assert.That(result.Data.MinCandidateScore, Is.EqualTo(49),
            "Phase 2 saw the Score=49 candidate but excluded it — MinCandidateScore should be 49.");

        Assert.That(result.Data.Directive, Does.Contain("scoreThreshold").And.Contain("49"),
            "Directive must cite both the scoreThreshold and the candidate's score of 49.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T20 – Asyncify macro, default params, Score=70 candidate with a caller →
    //        Phase 2 bridges, Phase 3 uplifts the caller
    // Confirms the full pipeline runs when the candidate score meets the threshold.
    // ══════════════════════════════════════════════════════════════════════════

    private const string LoadListHighScoreWithCallerSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public class RegionForm
        {
            private static object regionListBindingSource;

            [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
            public void loadList()
            {
                regionListBindingSource = CommonSearch.search(TripRegion.listAllSQL);
            }

            private static class CommonSearch { public static object search(string sql) => sql; }
            private static class TripRegion { public static string listAllSQL = "SELECT * FROM TripRegion"; }
        }

        public class CallerClass
        {
            private RegionForm _form = new RegionForm();
            public void DoWork() { _form.loadList(); }
        }
        """ + "\n" + AttrStub;

    [Test, CancelAfter(60000)]
    public async Task T20_Asyncify_DefaultParams_Score70WithCaller_BridgesAndUpliftsCallers()
    {
        SetSource(LoadListHighScoreWithCallerSource);

        var result = await _asyncifyTools.Asyncify();

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Data, Is.Not.Null);

        // Phase 2 bridges loadList (Score=70 ≥ scoreThreshold=60).
        // Phase 3 uplifts CallerClass.DoWork — the caller of the bridge wrapper.
        Assert.That(result.Data!.Succeeded, Is.GreaterThanOrEqualTo(2),
            "Expected ≥ 2 successes: Phase 2 bridge (loadList) + Phase 3 uplift (DoWork). " +
            $"Directive: {result.Data.Directive}");

        // Verify the workspace reflects both the bridge and the uplift.
        var solution = _workspaceManager.CurrentSolution;
        var doc = solution?.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, "RegionForm.cs", StringComparison.OrdinalIgnoreCase));

        Assert.That(doc, Is.Not.Null, "RegionForm.cs must be present in the workspace.");
        var updatedSource = (await doc!.GetTextAsync()).ToString();

        Assert.That(updatedSource, Does.Contain("loadListAsync"),
            "Phase 2 bridge must produce loadListAsync.");
        Assert.That(updatedSource, Does.Contain("DoWorkAsync"),
            "Phase 3 uplift must produce DoWorkAsync as the async version of DoWork.");
    }

    [Test, CancelAfter(30000)]
    public async Task T17_UpliftCallers_QualifiedStaticCall_PreservesTypeQualifier()
    {
        SetSource(QualifiedStaticCallSource, "QualifiedCall.cs");

        var result = await _asyncifyTools.UpliftCallers(
            targets: [new UpliftTarget { BridgedMethodName = "SyncMethod" }],
            maxCallersPerMethod: 5,
            dryRun: false,
            propagateCancellationTokens: true);

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Summary.Succeeded, Is.EqualTo(1),
            "DoWork should be uplifted as the caller of SyncMethod.");

        // Read back the updated document from the in-memory workspace.
        var solution = _workspaceManager.CurrentSolution;
        var doc = solution?.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, "QualifiedCall.cs", StringComparison.OrdinalIgnoreCase));

        Assert.That(doc, Is.Not.Null, "QualifiedCall.cs document must exist in workspace after uplift.");
        var sourceText = await doc!.GetTextAsync();
        var updatedSource = sourceText.ToString();

        // The qualifier 'Service.' must be preserved in the rewritten async body.
        Assert.That(updatedSource, Does.Contain("Service.SyncMethodAsync"),
            "Qualified call 'Service.SyncMethod' must be rewritten as 'Service.SyncMethodAsync', not plain 'SyncMethodAsync'.");

        // Confirm the unqualified form is absent from the DoWorkAsync body.
        // (The bridge body 'DoWorkAsync().GetAwaiter().GetResult()' is unqualified, but that is
        //  the caller-bridge call, not the rewritten callee call — so we check the specific pattern.)
        Assert.That(updatedSource, Does.Not.Contain("await SyncMethodAsync("),
            "Unqualified 'await SyncMethodAsync(' must not appear; the qualifier 'Service.' must be kept.");
    }
}
