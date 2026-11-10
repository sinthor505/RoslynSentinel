using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

// Stale-read guard through AsyncBatchEngine (plan_stale_read_guard_followups.md, Step 5b, test row H).
// PropagateCancellationTokenBatchAsync writes through ApplyProposedChangesAsync with RefuseIfObservedStaleQuiet:
// a target this agent READ and that changed on disk since is reported Failed with the StaleRead code and nothing
// is written; an unobserved target is written normally. Runs on a real PersistentWorkspaceManager (never
// FakeWorkspaceManager, which holds its own copy of the stale check).
[TestFixture]
[Category("AsyncBatchEngine")]
public class StaleReadBatchEngineTests
{
    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

    private IWorkspaceManager _workspaceManager;
    private AsyncBatchEngine _batchEngine;
    private string _tempDir;

    // The forwarder has an overload that takes a CancellationToken, so the batch has a real edit to write.
    private const string ForwardingSource = """
        using System.Threading;
        using System.Threading.Tasks;

        public class TripService
        {
            public async Task RunAsync(CancellationToken cancellationToken)
            {
                await WorkAsync();
            }

            public Task WorkAsync() => Task.CompletedTask;

            public Task WorkAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        """;

    [SetUp]
    public void SetUp()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "StaleReadBatchEngineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(
            _workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _batchEngine = Battery.Advanced.TestEnginesAdvanced.AsyncBatchEngine(
            _workspaceManager,
            Battery.Advanced.TestEnginesAdvanced.AsyncOptimizationEngine(_workspaceManager),
            validationEngine,
            Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager),
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);
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
    private void SetSource(string source, string fileName)
    {
        var path = PathOf(fileName);
        File.WriteAllText(path, source);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [(fileName, source, path)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "ReadFile", null, StartSeqAfterEveryWrite));

    private static PropagateCtBatchInput InputFor(string path) => new PropagateCtBatchInput
    {
        Targets = [new PropagateCtFileTarget { FilePath = path }],
        FlagFailures = false,
    };

    [Test, CancelAfter(60000)]
    public async Task PropagateCtBatch_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothing()
    {
        SetSource(ForwardingSource.Replace("\r\n", "\n"), "TripService.cs");
        var path = PathOf("TripService.cs");

        // The agent READ the file (RecordObserved, as the read tools do), then another session rewrote it on disk.
        var observed = File.ReadAllText(path);
        AsAgent("agentA");
        ((IObservedContentLedger)_workspaceManager).RecordObserved(new FilePathWrapper(path), observed);
        var external = observed + "// changed on disk by another session\n";
        File.WriteAllText(path, external);

        var result = await _batchEngine.PropagateCancellationTokenBatchAsync(InputFor(path));

        Assert.That(result.Applied, Is.Empty, "A refused write must not be reported as applied.");
        Assert.That(result.TotalForwarded, Is.EqualTo(0), "A refused write must not count its forwarded call sites.");
        Assert.That(result.Failed, Has.Count.EqualTo(1), "A refused write must be reported as one Failed file.");
        Assert.That(result.Failed[0].Reason, Does.Contain("StaleRead"),
            "The Failed reason must name the StaleRead refusal. Reason: " + result.Failed[0].Reason);
        Assert.That(File.ReadAllText(path), Is.EqualTo(external),
            "A refused write must leave the externally written disk text untouched.");
    }

    [Test, CancelAfter(60000)]
    public async Task PropagateCtBatch_NeverObservedThenChangedOnDisk_FoldPendingOnceThenWrites()
    {
        SetSource(ForwardingSource.Replace("\r\n", "\n"), "TripService.cs");
        var path = PathOf("TripService.cs");
        var external = File.ReadAllText(path) + "// changed on disk by another session\n";
        File.WriteAllText(path, external);
        AsAgent("agentA");

        // The agent never read the file, so no StaleRead applies. The write chokepoint still sees memory differ from
        // disk and refuses the first call once with FoldPending, folding the file in memory as it does. The retry
        // then writes normally.
        var first = await _batchEngine.PropagateCancellationTokenBatchAsync(InputFor(path));

        Assert.That(first.Applied, Is.Empty, "The first call must be refused once while memory differs from disk.");
        Assert.That(first.Failed, Has.Count.EqualTo(1));
        Assert.That(first.Failed[0].Reason, Does.Contain("FoldPending"),
            "The first refusal must carry the FoldPending code. Reason: " + first.Failed[0].Reason);
        Assert.That(File.ReadAllText(path), Is.EqualTo(external),
            "A FoldPending refusal must leave the externally written disk text untouched.");

        var result = await _batchEngine.PropagateCancellationTokenBatchAsync(InputFor(path));

        Assert.That(result.Failed, Is.Empty, "An unobserved target must be written normally on the retry under the quiet policy.");
        Assert.That(result.Applied, Has.Count.EqualTo(1));
        Assert.That(File.ReadAllText(path), Is.Not.EqualTo(external), "The unobserved file must be modified on the retry.");
    }
}
