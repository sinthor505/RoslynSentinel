using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

// Stale-read guard through CommentingTools.BulkComment (plan_stale_read_guard_followups.md, Steps 5b and 5d, test row H).
// The seed phase writes through ApplyProposedChangesAsync with RefuseIfObservedStaleQuiet. A target this agent READ
// and that changed on disk since is refused: the call reports seed_write_refused with the StaleRead summary, and no
// seed or comment changes are written. An unobserved target is seeded normally. Runs on a real PersistentWorkspaceManager.
[TestFixture]
[Category("BulkComment")]
public class StaleReadCommentingTests
{
    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

    private IWorkspaceManager _workspaceManager;
    private CommentingTools _commentingTools;
    private string _tempDir;

    // A public member with no [ContentHash] stamp gives the seed phase something to write.
    private const string Source = """
        public class OrderService
        {
            public int Count() => 1;
        }
        """;

    [SetUp]
    public void SetUp()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), "StaleReadCommentingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        var refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(
            _workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var commentingEngine = new CommentingEngine(_workspaceManager, refactoringEngine, new FakeLlmClient());
        _commentingTools = new CommentingTools(commentingEngine, _workspaceManager, NullLogger<CommentingTools>.Instance);
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

    [Test, CancelAfter(60000)]
    public async Task BulkComment_File_ObservedThenChangedOnDisk_SeedRefusedWithStaleReadAndWritesNothing()
    {
        SetSource(Source.Replace("\r\n", "\n"), "OrderService.cs");
        var path = PathOf("OrderService.cs");

        // The agent READ the file (RecordObserved, as the read tools do), then another session rewrote it on disk.
        var observed = File.ReadAllText(path);
        AsAgent("agentA");
        ((IObservedContentLedger)_workspaceManager).RecordObserved(new FilePathWrapper(path), observed);
        var external = observed + "// changed on disk by another session\n";
        File.WriteAllText(path, external);

        var result = await _commentingTools.BulkCommentTool(
            reason: "test message", scope: ToolScope.file, filePath: path, dryRun: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        var reasons = result.SuccessData!.SkippedByReason.Keys.ToList();
        Assert.That(reasons, Has.Some.Contain("seed_write_refused"),
            "The refused seed write must abort the call with seed_write_refused. Reasons: " + string.Join(" | ", reasons));
        Assert.That(reasons, Has.Some.Contain("StaleRead"),
            "The abort reason must carry the StaleRead summary. Reasons: " + string.Join(" | ", reasons));
        Assert.That(result.SuccessData.Seeded, Is.Zero, "A refused seed phase must not count any seeded member.");
        Assert.That(File.ReadAllText(path), Is.EqualTo(external),
            "A refused seed write must leave the externally written disk text untouched.");
    }

    [Test, CancelAfter(60000)]
    public async Task BulkComment_File_ObservedThenChangedOnDisk_PerFileWriteRefusedWithStaleReadAsync()
    {
        // Every taggable member already carries a [ContentHash] whose hash no longer matches its body, so the seed
        // phase has nothing to write and FindStaleMembersAsync reports the member as stale for the per-file phase.
        // The attribute class is declared in the fixture with a primary constructor (not a taggable member) so the
        // seed phase does not try to stamp it.
        var stagedSource = """
            using System;

            public sealed class ContentHashAttribute(string purpose, string hash) : Attribute { }

            public class OrderService
            {
                [ContentHash("Comment", "stale-hash")]
                public int Count() => 1;
            }
            """.Replace("\r\n", "\n");
        SetSource(stagedSource, "OrderService.cs");
        var path = PathOf("OrderService.cs");

        // The agent READ the file (RecordObserved, as the read tools do), then another session rewrote it on disk.
        var observed = File.ReadAllText(path);
        AsAgent("agentA");
        ((IObservedContentLedger)_workspaceManager).RecordObserved(new FilePathWrapper(path), observed);
        var external = observed + "// changed on disk by another session\n";
        File.WriteAllText(path, external);

        var result = await _commentingTools.BulkCommentTool(
            reason: "test message", scope: ToolScope.file, filePath: path, dryRun: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData!.Seeded, Is.Zero, "The seed phase must have nothing to write for this fixture.");
        var reasons = result.SuccessData.SkippedByReason.Keys.ToList();
        Assert.That(reasons, Has.Some.Contain("write refused"),
            "The per-file write must be refused with a write-refused reason. Reasons: " + string.Join(" | ", reasons));
        Assert.That(reasons, Has.Some.Contain("StaleRead"),
            "The per-file refusal must carry the StaleRead summary. Reasons: " + string.Join(" | ", reasons));
        Assert.That(File.ReadAllText(path), Is.EqualTo(external),
            "A refused per-file write must leave the externally written disk text untouched.");
    }

    [Test, CancelAfter(60000)]
    public async Task BulkComment_File_NeverObservedThenChangedOnDisk_SeedsNormally()
    {
        SetSource(Source.Replace("\r\n", "\n"), "OrderService.cs");
        var path = PathOf("OrderService.cs");
        var external = File.ReadAllText(path) + "// changed on disk by another session\n";
        File.WriteAllText(path, external);
        AsAgent("agentA");

        var result = await _commentingTools.BulkCommentTool(
            reason: "test message", scope: ToolScope.file, filePath: path, dryRun: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.ToString());
        Assert.That(result.SuccessData, Is.Not.Null);
        var reasons = result.SuccessData!.SkippedByReason.Keys.ToList();
        Assert.That(reasons, Has.None.Contains("seed_write_refused"),
            "An unobserved target must be seeded normally under the quiet policy. Reasons: " + string.Join(" | ", reasons));
        Assert.That(File.ReadAllText(path), Is.Not.EqualTo(external), "The unobserved file must be modified.");
    }
}
