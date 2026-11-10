using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

// Stale-read guard through the Asyncify family (plan_stale_read_guard_followups.md, Steps 5a and 5d, test row H).
// Every Asyncify chokepoint call passes RefuseIfObservedStaleQuiet: a target this agent READ (RecordObserved) and that
// changed on disk since is refused with StaleRead and nothing is written; an unobserved target is written normally.
// Runs on a real PersistentWorkspaceManager (never FakeWorkspaceManager, which holds its own copy of the stale check).
[TestFixture]
[Category("AsyncifyTools")]
public class StaleReadAsyncifyTests
{
    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

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

    private const string SvcSource = """
        using System.Threading;
        using System.Threading.Tasks;
        public class Svc
        {
            public async Task DoWorkAsync() { await Task.Delay(1); }
        }
        """;

    // Same flag-phase fixture shape as SentinelAsyncifyToolsTests.FlagPhaseSource (LoadOne scores 55, above minScore 50).
    private const string FlagPhaseSource = """
        using System.Threading.Tasks;

        public class OrderService
        {
            public int LoadOne(Task<int> t) { return t.Result; }

            public int LoadTwo(Task<int> t)
            {
                return t.Result;
            }
        }
        """;

    private const string HandlerSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public class TripService
        {
            [MigrationCandidate("HandlerToAsyncCandidate", Score = 70, Reason = "extracted handler body", FlaggedDate = "2026-06-01")]
            public int GetTrips(int companyId)
            {
                return companyId;
            }
        }
        """ + "\n" + AttrStub;

    [SetUp]
    public void SetUp()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "StaleReadAsyncifyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var antiPatternEngine = Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager);
        var asyncOptEngine = Battery.Advanced.TestEnginesAdvanced.AsyncOptimizationEngine(_workspaceManager);
        var asyncBatchEngine = Battery.Advanced.TestEnginesAdvanced.AsyncBatchEngine(
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
            Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            validationEngine,
            failureRouter,
            new MigrationLedger(),
            NullLogger<AsyncifyTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>Absolute path of a fixture file inside this test's own temp directory.</summary>
    private string PathOf(string fileName) => Path.Combine(_tempDir, fileName);

    // Written to disk under the per-test temp directory (ApplyProposedChangesAsync reads the pre-image from disk).
    private void SetSource(string source, string fileName = "RegionForm.cs")
    {
        var path = PathOf(fileName);
        File.WriteAllText(path, source);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [(fileName, source, path)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "ReadFile", null, StartSeqAfterEveryWrite));

    // The agent READ the file (RecordObserved, as the read tools do), then another session rewrote it on disk.
    // Returns the external disk text that a refused write must leave in place.
    private string ObserveThenChangeOnDisk(string fileName)
    {
        var path = PathOf(fileName);
        var observed = File.ReadAllText(path);
        AsAgent("agentA");
        ((IObservedContentLedger)_workspaceManager).RecordObserved(new FilePathWrapper(path), observed);
        var external = observed + "// changed on disk by another session\n";
        File.WriteAllText(path, external);
        return external;
    }

    private static FlagCandidateTarget LoadOneTarget(string filePath) => new FlagCandidateTarget
    {
        FilePath = filePath,
        MethodName = "LoadOne",
        Pattern = "AsyncBridgeCandidate",
        Score = 55,
        Reason = "blocking-calls:40 service-class:15",
    };

    // ══════════════════════════════════════════════════════════════════════════
    // AddCancellationToken (direct chokepoint site, Step 5a; counts fixed in Step 5d)
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task AddCancellationToken_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothing()
    {
        SetSource(SvcSource, "Svc.cs");
        var external = ObserveThenChangeOnDisk("Svc.cs");

        var result = await _asyncifyTools.AddCancellationTokenTool(
            reason: "test message",
            targets: [new BatchTarget { FilePath = PathOf("Svc.cs") }],
            dryRun: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Succeeded, Is.Zero,
            "A refused apply must not leave the file counted as Succeeded.");
        Assert.That(result.SuccessData.Failed, Is.GreaterThanOrEqualTo(1),
            "A refused apply must count the file as Failed.");
        var reasons = result.SuccessData.Failures.Select(f => f.Reason).ToList();
        Assert.That(reasons, Has.Some.Contain("StaleRead"),
            "The failure Reason must name the StaleRead refusal. Reasons: " + string.Join(" | ", reasons));
        Assert.That(File.ReadAllText(PathOf("Svc.cs")), Is.EqualTo(external),
            "A refused apply must leave the externally written disk text untouched.");
    }

    [Test, CancelAfter(60000)]
    public async Task AddCancellationToken_NeverObservedThenChangedOnDisk_FoldPendingOnceThenWrites()
    {
        SetSource(SvcSource, "Svc.cs");
        var path = PathOf("Svc.cs");
        File.WriteAllText(path, File.ReadAllText(path) + "// changed on disk by another session\n");
        var external = File.ReadAllText(path);
        AsAgent("agentA");

        // The agent never read the file, so no StaleRead applies. The write chokepoint still sees memory differ from
        // disk and refuses the first call once with FoldPending, folding the file in memory as it does. The retry
        // then writes normally.
        var first = await _asyncifyTools.AddCancellationTokenTool(
            reason: "test message",
            targets: [new BatchTarget { FilePath = PathOf("Svc.cs") }],
            dryRun: false);

        Assert.That(!first.IsError, Is.True, first.ErrorData?.ToString());
        Assert.That(first.SuccessData, Is.Not.Null);
        Assert.That(first.SuccessData!.Succeeded, Is.Zero, "The first call must be refused once while memory differs from disk.");
        var firstReasons = first.SuccessData.Failures.Select(f => f.Reason).ToList();
        Assert.That(firstReasons, Has.Some.Contains("FoldPending"),
            "The first refusal must carry the FoldPending code. Reasons: " + string.Join(" | ", firstReasons));
        Assert.That(File.ReadAllText(path), Is.EqualTo(external),
            "A FoldPending refusal must leave the externally written disk text untouched.");

        var result = await _asyncifyTools.AddCancellationTokenTool(
            reason: "test message",
            targets: [new BatchTarget { FilePath = PathOf("Svc.cs") }],
            dryRun: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Succeeded, Is.GreaterThanOrEqualTo(1),
            "An unobserved target must be written normally on the retry under the quiet policy.");
        Assert.That(result.SuccessData.Failed, Is.Zero);
        var onDisk = File.ReadAllText(path);
        Assert.That(onDisk, Does.Contain("CancellationToken"));
        Assert.That(onDisk, Is.Not.EqualTo(external), "The unobserved file must be modified on the retry.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FlagAsyncMigrationCandidates scope=targets (DescribeRefusedApply site, Step 5d)
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task FlagAsyncMigrationCandidates_TargetsScope_ObservedThenChangedOnDisk_ReportsStaleReadNotValidation()
    {
        SetSource(FlagPhaseSource, "OrderService.cs");
        var external = ObserveThenChangeOnDisk("OrderService.cs");

        var result = await _asyncifyTools.FlagAsyncMigrationCandidates(
            scope: "targets",
            flagTargets: [LoadOneTarget(PathOf("OrderService.cs"))],
            dryRun: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Succeeded, Is.Zero,
            "A refused flag write must not leave the method counted as Succeeded.");
        Assert.That(result.SuccessData.Failed, Is.EqualTo(1));
        var reportedText = string.Join(" | ", result.SuccessData.Failures.Select(f => f.Reason))
            + " | " + result.SuccessData.Directive;
        Assert.That(reportedText, Does.Contain("StaleRead"),
            "The refusal must be reported as StaleRead, not as a generic write failure. Reported: " + reportedText);
        Assert.That(reportedText, Does.Not.Contain("Validation: 0 error(s)"),
            "A refusal must not be reported as a validation failure with zero errors. Reported: " + reportedText);
        Assert.That(File.ReadAllText(PathOf("OrderService.cs")), Is.EqualTo(external),
            "A refused flag write must leave the externally written disk text untouched.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // EventHandlersToAsync (HandlerToAsyncCore records the refusal as Failed, Step 5d)
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(60000)]
    public async Task EventHandlersToAsync_ObservedThenChangedOnDisk_RecordsItemFailedWithStaleRead()
    {
        SetSource(HandlerSource, "TripService.cs");
        var external = ObserveThenChangeOnDisk("TripService.cs");

        var result = await _asyncifyTools.EventHandlersToAsyncTool(reason: "test message");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Succeeded, Is.Zero,
            "A refused bridge write must not record the handler as Succeeded.");
        Assert.That(result.SuccessData.Failed, Is.EqualTo(1));
        Assert.That(result.SuccessData.Failures, Has.Count.EqualTo(1));
        Assert.That(result.SuccessData.Failures[0].Reason, Does.Contain("StaleRead"),
            "Reason was: " + result.SuccessData.Failures[0].Reason);
        Assert.That(File.ReadAllText(PathOf("TripService.cs")), Is.EqualTo(external),
            "A refused bridge write must leave the externally written disk text untouched.");
    }
}
