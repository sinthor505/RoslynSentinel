using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for AsyncifyTools public MCP methods:
///   T1  – ScanAsyncMigrationCandidatesTool, no solution -> SolutionNotLoaded
///   T2  – ScanAsyncMigrationCandidatesTool, loadList with [MigrationCandidate] -> finding returned
///   T3  – ScanAsyncMigrationCandidatesTool, summarize=true -> MigrationScanSummary with 5 bucket keys
///   T4  – ScanAsyncMigrationCandidatesTool, minScore above all scores -> empty list
///   T5  – GetAsyncMigrationProgressTool, no solution -> SolutionNotLoaded
///   T6  – GetAsyncMigrationProgressTool, solution with async methods -> report populated
///   T7  – FlagAsyncMigrationCandidates, no solution -> SolutionNotLoaded
///   T8  – BridgeAsyncMethodsTool, empty targets -> 0 attempted, success
///   T9  – FlagAsyncMigrationCandidates, scope=targets, DryRun=true -> succeeded
///   T10 – BridgeAsyncMethodsTool, DryRun=true -> all items attempted, none failed
///   T11 – AddCancellationTokenTool, DryRun=true -> returns without error
///   T12 – PropagateCancellationTokenTool, empty targets -> 0 attempted, success
///   T13 – ExtractEventHandlersTool, DryRun=true, missing ContextSnippet -> failed items
///   T14 – UpliftCallersTool, empty targets -> 0 attempted, SuggestedPropagateTargets empty
///   T15 – EventHandlersToAsyncTool, no solution -> SolutionNotLoaded
///   T17 – UpliftCallersTool, qualified static call -> qualifier preserved (Service.SyncMethodAsync not SyncMethodAsync)
///   T19 – AsyncifyTool macro, default params, Score=49 candidate -> Phase 2 skips it (below aligned threshold of 50)
///   T20 – AsyncifyTool macro, default params, Score=70 candidate with caller -> bridges AND uplifts (Phase 2 + 3)
/// </summary>
[TestFixture]
[Category("AsyncifyTools")] // sentinel:auto-category
public class AsyncifyToolsTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncifyTools _asyncifyTools;
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

    // loadList source adapted from Avaal3 RegionForm.cs -> WinForms deps stripped.
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

    // loadList without attribute -> for flag_migration_candidates tests.
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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "AsyncifyToolsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
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

        _asyncifyTools = new AsyncifyTools(
            antiPatternEngine,
            asyncOptEngine,
            asyncBatchEngine,
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            validationEngine,
            failureRouter,
            new MigrationLedger(),
            NullLogger<AsyncifyTools>.Instance);
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

    /// <summary>Absolute path of a fixture file inside this test's own temp directory.</summary>
    private string PathOf(string fileName) => Path.Combine(_tempDir, fileName);

    // The fixture file is written to disk under the per-test temp directory: ApplyProposedChangesAsync reads the
    // pre-image from disk, so a bare relative name resolved against the test process's working directory (the shared
    // bin folder) and compared every write against whatever an earlier run had left there.
    private void SetSource(string source, string fileName = "RegionForm.cs")
    {
        var path = PathOf(fileName);
        File.WriteAllText(path, source);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [(fileName, source, path)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T1 – ScanAsyncMigrationCandidatesTool, no solution -> SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T1_ScanAsyncMigrationCandidates_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.ScanAsyncMigrationCandidatesTool(reason: "test message");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2 – ScanAsyncMigrationCandidatesTool, loadList with [MigrationCandidate] -> finding returned
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T2_ScanAsyncMigrationCandidates_LoadListWithAttribute_FindsCandidate()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidatesTool(reason: "test message");

        Assert.That(!result.IsError, Is.True);
        var findings = result.SuccessData as List<MigrationCandidateFinding>;
        Assert.That(findings, Is.Not.Null, "SuccessDetails should be List<MigrationCandidateFinding> for summarize=false.");
        Assert.That(findings!.Any(f => f.MethodName == "loadList"), Is.True,
            "loadList should be found as a migration candidate.");
        var finding = findings!.First(f => f.MethodName == "loadList");
        Assert.That(finding.ClassName, Is.EqualTo("RegionForm"));
        Assert.That(finding.Pattern, Does.Contain("AsyncBridge"));
        Assert.That(finding.Score, Is.EqualTo(50));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T3 – ScanAsyncMigrationCandidatesTool, summarize=true -> MigrationScanSummary, 5 bucket keys
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T3_ScanAsyncMigrationCandidates_Summarize_ReturnsMigrationScanSummary()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidatesTool(reason: "test message", summarize: true);

        Assert.That(!result.IsError, Is.True);
        var summary = result.SuccessData as MigrationScanSummary;
        Assert.That(summary, Is.Not.Null, "SuccessDetails should be MigrationScanSummary for summarize=true.");
        Assert.That(summary!.TotalCandidates, Is.EqualTo(1));

        var expectedBuckets = new[] { "<0", "0-25", "26-50", "51-75", "76plus" };
        foreach (var bucket in expectedBuckets)
        {
            Assert.That(summary.ByScoreBucket.ContainsKey(bucket), Is.True, $"Bucket key '{bucket}' must be present.");
        }

        Assert.That(summary.ByClass, Is.Not.Null);
        Assert.That(summary.ByClass.Count, Is.EqualTo(1));
        Assert.That(summary.ByClass[0].ClassName, Is.EqualTo("RegionForm"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T4 – ScanAsyncMigrationCandidatesTool, minScore above all -> empty list
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T4_ScanAsyncMigrationCandidates_MinScoreAboveAll_ReturnsEmptyList()
    {
        SetSource(LoadListFlaggedSource);

        var result = await _asyncifyTools.ScanAsyncMigrationCandidatesTool(reason: "test message", minScore: 100);

        Assert.That(!result.IsError, Is.True);
        var findings = result.SuccessData as List<MigrationCandidateFinding>;
        Assert.That(findings, Is.Not.Null);
        Assert.That(findings!.Count, Is.Zero,
            "minScore=100 should filter out loadList which has Score=50.");
        Assert.That(result.TotalRecords, Is.Zero);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T5 – GetAsyncMigrationProgressTool, no solution -> SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T5_GetAsyncMigrationProgress_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.GetAsyncMigrationProgressTool(reason: "test message");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T6 – GetAsyncMigrationProgressTool, solution with async methods -> report populated
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

        var result = await _asyncifyTools.GetAsyncMigrationProgressTool(reason: "test message");

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Null);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.TotalAsyncMethods, Is.EqualTo(2));
        Assert.That(result.SuccessData.WithCancellationToken, Is.EqualTo(1));
        Assert.That(result.SuccessData.WithoutCancellationToken, Is.EqualTo(1));
        Assert.That(result.SuccessData.CancellationTokenPct, Is.EqualTo(50));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T7 – FlagAsyncMigrationCandidates, no solution -> SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T7_FlagAsyncMigrationCandidates_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.FlagAsyncMigrationCandidates(
            scope: "targets",
            flagTargets: []);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T8 – BridgeAsyncMethodsTool, empty targets -> 0 attempted, success
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T8_BridgeAsyncMethods_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.BridgeAsyncMethodsTool(reason: "test message", targets: []);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Summary.Attempted, Is.Zero);
        Assert.That(result.SuccessData.SuggestedUpliftTargets, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T9 – FlagAsyncMigrationCandidates, scope=targets, DryRun=true -> succeeded
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
                    FilePath = PathOf("RegionForm.cs"),
                    MethodName = "loadList",
                    Pattern = "AsyncBridgeCandidate",
                    Score = 50,
                    Reason = "calls-CommonSearch:30 calls-obsolete-wrapper:20",
                }
            ],
            dryRun: true);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.BreakerOpen, Is.False);
        Assert.That(result.SuccessData.Succeeded, Is.EqualTo(1),
            "loadList should be flagged (DryRun=true - changes computed but not written).");
        Assert.That(result.SuccessData.Failed, Is.Zero);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T10 – BridgeAsyncMethodsTool, DryRun=true -> all items attempted, none failed
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T10_BridgeAsyncMethods_DryRun_AllItemsAttempted()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.BridgeAsyncMethodsTool(
            reason: "test message",
            targets: [new BatchTarget { FilePath = PathOf("RegionForm.cs"), MethodNames = ["loadList"] }],
            dryRun: true);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Summary.Attempted, Is.EqualTo(1));
        Assert.That(result.SuccessData.Summary.Failed, Is.Zero);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T11 – AddCancellationTokenTool, DryRun=true -> returns without error
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

        var result = await _asyncifyTools.AddCancellationTokenTool(
            reason: "test message",
            targets: [new BatchTarget { FilePath = PathOf("Svc.cs") }],
            dryRun: true);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.BreakerOpen, Is.False);
        Assert.That(result.SuccessData.Attempted, Is.EqualTo(1));
        Assert.That(result.SuccessData.Succeeded, Is.EqualTo(1));
        Assert.That(result.SuccessData.Failed, Is.EqualTo(0));
        Assert.That(result.SuccessData.Skipped, Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T12 – PropagateCancellationTokenTool, empty targets -> 0 attempted, success
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T12_PropagateCancellationToken_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.PropagateCancellationTokenTool(
            reason: "test message",
            targets: [],
            dryRun: true);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Attempted, Is.Zero);
        Assert.That(result.SuccessData.BreakerOpen, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T13 – ExtractEventHandlersTool, DryRun=true, missing ContextSnippet -> failed items
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(10000)]
    public async Task T13_ExtractEventHandlers_MissingContextSnippet_ReturnsFailed()
    {
        SetSource(LoadListUnflaggedSource);

        var result = await _asyncifyTools.ExtractEventHandlersTool(
            reason: "test message",
            targets:
            [
                new HandlerExtractTarget
                {
                    FilePath = PathOf("RegionForm.cs"),
                    NewMethodName = "DoSearch",
                    ContextSnippet = "",   // intentionally empty -> validation failure
                }
            ],
            dryRun: true);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Failed, Is.EqualTo(1),
            "An empty ContextSnippet should produce one failed item.");
        Assert.That(result.SuccessData.Failures, Is.Not.Empty);
        Assert.That(result.SuccessData.Failures[0].Reason, Does.Contain("ContextSnippet"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T14 – UpliftCallersTool, empty targets -> 0 attempted, SuggestedPropagateTargets empty
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T14_UpliftCallers_EmptyTargets_ZeroAttempted()
    {
        SetSource("public class C { public void M() {} }", "C.cs");

        var result = await _asyncifyTools.UpliftCallersTool(reason: "test message", targets: []);

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Summary.Attempted, Is.Zero);
        Assert.That(result.SuccessData.SuggestedPropagateTargets, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T15 – EventHandlersToAsyncTool, no solution -> SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T15_EventHandlersToAsync_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.EventHandlersToAsyncTool(reason: "test message");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T16 – AsyncifyTool with maxIterations/maxRuntimeSeconds params, no solution -> SolutionNotLoaded
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task T16_Asyncify_WithLimitParams_NoSolution_ReturnsSolutionNotLoaded()
    {
        var result = await _asyncifyTools.AsyncifyTool(reason: "test message", maxRuntimeSeconds: 30, maxIterations: 10);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(MigrationErrorCode.SolutionNotLoaded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T17 – UpliftCallersTool, qualified static call -> type qualifier preserved
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

        var result = await _asyncifyTools.UpliftCallersTool(
            reason: "test message",
            targets: [new UpliftTarget { BridgedMethodName = "GetData" }],
            maxCallersPerMethod: 5,
            dryRun: false,
            propagateCancellationTokens: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution?.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, PathOf("Chained.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.That(doc, Is.Not.Null);
        var text = (await doc!.GetTextAsync()).ToString();

        // The await must be wrapped in parens so the member access binds to the awaited value:
        //   (await GetDataAsync(cancellationToken)).Trim()
        // not:
        //   await GetDataAsync(cancellationToken).Trim()
        Assert.That(text, Does.Contain("(await "),
            "chained await must be parenthesised: `(await GetDataAsync(...)).Member`.");
        Assert.That(text, Does.Not.Contain("await DataHelper.GetDataAsync(cancellationToken)."),
            "bare `await GetDataAsync(cancellationToken).Member` must not appear - parens required.");
        Assert.That(text, Does.Not.Contain("await GetDataAsync(cancellationToken)."),
            "bare `await GetDataAsync(cancellationToken).Member` must not appear - parens required.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T19 – AsyncifyTool macro, default params, Score=49 candidate -> Phase 2 skips it
    // Verifies the lower boundary: a candidate one point below DefaultScoreThreshold=50
    // is found by Phase 2 but excluded, so Phase 3 (uplift) is never entered.
    // ══════════════════════════════════════════════════════════════════════════

    // Score=49 -> one point below the aligned DefaultScoreThreshold=50.
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
        // loadList has Score=49, one below DefaultScoreThreshold=50 -> must not be bridged.
        SetSource(LoadListLowScoreSource);

        var result = await _asyncifyTools.AsyncifyTool(reason: "test message");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);

        Assert.That(result.SuccessData!.Succeeded, Is.Zero,
            "loadList (Score=49) is below scoreThreshold=50 and must not be bridged.");

        Assert.That(result.SuccessData.MinCandidateScore, Is.EqualTo(49),
            "Phase 2 saw the Score=49 candidate but excluded it - MinCandidateScore should be 49.");

        Assert.That(result.SuccessData.Directive, Does.Contain("scoreThreshold").And.Contain("49"),
            "Directive must cite both the scoreThreshold and the candidate's score of 49.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T20 – AsyncifyTool macro, default params, Score=70 candidate with a caller ->
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

        var result = await _asyncifyTools.AsyncifyTool(reason: "test message");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);

        // Phase 2 bridges loadList (Score=70 ≥ scoreThreshold=60).
        // Phase 3 uplifts CallerClass.DoWork -> the caller of the bridge wrapper.
        Assert.That(result.SuccessData!.Succeeded, Is.GreaterThanOrEqualTo(2),
            "Expected ≥ 2 successes: Phase 2 bridge (loadList) + Phase 3 uplift (DoWork). " +
            $"Directive: {result.SuccessData.Directive}");

        // Verify the workspace reflects both the bridge and the uplift.
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution?.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, PathOf("RegionForm.cs"), StringComparison.OrdinalIgnoreCase));

        Assert.That(doc, Is.Not.Null, "RegionForm.cs must be present in the workspace.");
        var updatedSource = (await doc!.GetTextAsync()).ToString();

        Assert.That(updatedSource, Does.Contain("loadListAsync"),
            "Phase 2 bridge must produce loadListAsync.");
        Assert.That(updatedSource, Does.Contain("DoWorkAsync"),
            "Phase 3 uplift must produce DoWorkAsync as the async version of DoWork.");
    }

    // Phase 3b: an AsyncHandlerCandidate flag on an already-async handler is stripped, but the strip write is refused
    // because the file is read-only. The per-item Reason is not on the AsyncifyTool result; it is written to the
    // operation blob, so the assertion reads the blob JSON.
    [Test, CancelAfter(60000)]
    public async Task T22_Asyncify_HandlerConvert_StaleFlagStripWriteRefused_ReasonCarriesCleanupNote()
    {
        var source = """
            using System;
            using System.Threading.Tasks;

            public class RegionForm
            {
                [MigrationCandidate("AsyncHandlerCandidate", Score = 70, Reason = "stale flag on async handler", FlaggedDate = "2026-06-01")]
                public async void OnClick(object s, EventArgs e)
                {
                    await Task.Delay(0);
                }
            }
            """ + "\n" + AttrStub;

        SetSource(source);
        var path = PathOf("RegionForm.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _asyncifyTools.AsyncifyTool(reason: "test message");

            Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
            Assert.That(result.SuccessData, Is.Not.Null);
            Assert.That(result.SuccessData!.PhaseBreakdown.Handler.Skipped, Is.EqualTo(1),
                "The handler phase must reach the stale-flag branch for OnClick.");

            var blobPath = OperationBlobWriter.FindBlobPath(result.SuccessData.ChangeId, _tempDir);
            Assert.That(blobPath, Is.Not.Null.And.Not.Empty, "The operation blob for the run must be written under the temp solution root.");

            var blobText = File.ReadAllText(blobPath!);
            Assert.That(blobText, Does.Contain("stale-flag: already async - stripped (cleanup write refused: "),
                "The skipped OnClick item must carry the cleanup-refusal note.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Test, CancelAfter(30000)]
    public async Task T17_UpliftCallers_QualifiedStaticCall_PreservesTypeQualifier()
    {
        SetSource(QualifiedStaticCallSource, "QualifiedCall.cs");

        var result = await _asyncifyTools.UpliftCallersTool(
            reason: "test message",
            targets: [new UpliftTarget { BridgedMethodName = "SyncMethod" }],
            maxCallersPerMethod: 5,
            dryRun: false,
            propagateCancellationTokens: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Summary.Succeeded, Is.EqualTo(1),
            "DoWork should be uplifted as the caller of SyncMethod.");

        // Read back the updated document from the in-memory workspace.
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution?.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, PathOf("QualifiedCall.cs"), StringComparison.OrdinalIgnoreCase));

        Assert.That(doc, Is.Not.Null, "QualifiedCall.cs document must exist in workspace after uplift.");
        var sourceText = await doc!.GetTextAsync();
        var updatedSource = sourceText.ToString();

        // The qualifier 'Service.' must be preserved in the rewritten async body.
        Assert.That(updatedSource, Does.Contain("Service.SyncMethodAsync"),
            "Qualified call 'Service.SyncMethod' must be rewritten as 'Service.SyncMethodAsync', not plain 'SyncMethodAsync'.");

        // Confirm the unqualified form is absent from the DoWorkAsync body.
        // (The bridge body 'DoWorkAsync().GetAwaiter().GetResult()' is unqualified, but that is
        //  the caller-bridge call, not the rewritten callee call -> so we check the specific pattern.)
        Assert.That(updatedSource, Does.Not.Contain("await SyncMethodAsync("),
            "Unqualified 'await SyncMethodAsync(' must not appear; the qualifier 'Service.' must be kept.");
    }

    // ----------------------------------------------------------------------------------------------
    // T21 - UpliftCallers byte-exact output per line-ending style. Covers both body-rewrite sites:
    //   CallerClass.Load (sync caller -> ConvertToAsyncBridgeAsync + RewriteCallerAsyncBodyAsync) and
    //   AsyncCaller.RunAsync (already async -> SemanticBridgeCallRewriteAsync).
    // Untouched members keep their text and line terminator ("mixed": the one-line Untouched()
    // keeps its LF in a CRLF file); every changed line takes the file's dominant line ending.
    // ----------------------------------------------------------------------------------------------
    private const string UpliftPinSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public class CallerClass
        {
            // Loads data.
            public string Load(int id)
            {
                var raw = DataHelper.GetData(id);
                return DataHelper.GetData(id).Trim() + raw;
            }

            public void Untouched() { var x = 1; }
        }

        public class AsyncCaller
        {
            public async Task RunAsync(CancellationToken ct)
            {
                await Task.Yield();
                var s = DataHelper.GetData(1);
            }
        }

        public static class DataHelper
        {
            [Obsolete("Asyncify-bridge: call GetDataAsync instead.", false)]
            public static string GetData(int id) { return GetDataAsync(id).GetAwaiter().GetResult(); }

            public static async Task<string> GetDataAsync(int id, CancellationToken cancellationToken = default)
            { await Task.Delay(1, cancellationToken); return string.Empty; }
        }

        """;

    private const string UpliftPinExpected = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public class CallerClass
        {
            // Loads data.
            [Obsolete("Asyncify-bridge: call LoadAsync instead.", false)]
            public string Load(int id)
            {
                // Asyncify-bridge: synchronous wrapper over LoadAsync.
                return LoadAsync(id).GetAwaiter().GetResult();
            }

            public async Task<string> LoadAsync(int id, CancellationToken cancellationToken = default)
            {
                var raw = await DataHelper.GetDataAsync(id, cancellationToken);
                return (await DataHelper.GetDataAsync(id, cancellationToken)).Trim() + raw;
            }

            public void Untouched() { var x = 1; }
        }

        public class AsyncCaller
        {
            public async Task RunAsync(CancellationToken ct)
            {
                await Task.Yield();
                var s = await DataHelper.GetDataAsync(1, ct);
            }
        }

        public static class DataHelper
        {
            [Obsolete("Asyncify-bridge: call GetDataAsync instead.", false)]
            public static string GetData(int id) { return GetDataAsync(id).GetAwaiter().GetResult(); }

            public static async Task<string> GetDataAsync(int id, CancellationToken cancellationToken = default)
            { await Task.Delay(1, cancellationToken); return string.Empty; }
        }

        """;

    // "mixed" = CRLF file whose one-line Untouched() member ends in a stray LF.
    private static string WithLineEndings(string template, string style)
    {
        var lf = template.Replace("\r\n", "\n");
        if (style == "mixed")
        {
            return lf.Replace("\n", "\r\n").Replace("{ var x = 1; }\r\n", "{ var x = 1; }\n");
        }

        return lf.Replace("\n", style);
    }

    [TestCase("\n", TestName = "T21_UpliftCallers_ByteExact_LF")]
    [TestCase("\r\n", TestName = "T21_UpliftCallers_ByteExact_CRLF")]
    [TestCase("mixed", TestName = "T21_UpliftCallers_ByteExact_Mixed")]
    [CancelAfter(30000)]
    public async Task T21_UpliftCallers_ByteExact(string style)
    {
        SetSource(WithLineEndings(UpliftPinSource, style), "UpliftPin.cs");

        var result = await _asyncifyTools.UpliftCallersTool(
            reason: "test message",
            targets: [new UpliftTarget { BridgedMethodName = "GetData" }],
            maxCallersPerMethod: 5,
            dryRun: false,
            propagateCancellationTokens: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents)
            .First(d => string.Equals(d.FilePath, PathOf("UpliftPin.cs"), StringComparison.OrdinalIgnoreCase));
        var actual = (await doc.GetTextAsync()).ToString();
        var expected = WithLineEndings(UpliftPinExpected, style);

        if (actual != expected)
        {
            TestContext.Out.WriteLine("ACTUAL:");
            TestContext.Out.WriteLine(actual.Replace("\r", "<CR>").Replace("\n", "<LF>\n"));
        }

        Assert.That(actual, Is.EqualTo(expected).NoClip);
        Assert.That(File.ReadAllText(PathOf("UpliftPin.cs")), Is.EqualTo(expected).NoClip, "disk and workspace must agree");
    }
}
