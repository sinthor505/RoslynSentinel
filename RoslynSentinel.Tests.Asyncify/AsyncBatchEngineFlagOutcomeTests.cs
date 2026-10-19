using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// RunBridgeBatchAsync, validation-failure skip: the skip message must report the real outcome of the
/// NeedsManualReview flag write, never claim a flag that was refused.
/// </summary>
[TestFixture]
[Category("AsyncBatchEngine")]
public class AsyncBatchEngineFlagOutcomeTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncBatchEngine _batchEngine;
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

    // No using directives: the bridge adds Task<>, CancellationToken and [Obsolete] references that cannot bind,
    // so in-memory validation fails and the batch takes the "flag NeedsManualReview" path.
    private const string BrokenBridgeSource = """
        public class TripService
        {
            [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
            public int GetTrips(int companyId)
            {
                return companyId;
            }
        }
        """ + "\n" + AttrStub;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "AsyncBatchEngineFlagOutcomeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var validationEngine = new ValidationEngine(
            _workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _batchEngine = new AsyncBatchEngine(
            _workspaceManager,
            new AsyncOptimizationEngine(_workspaceManager),
            validationEngine,
            new AntiPatternEngine(_workspaceManager),
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);
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

    // The workspace document holds workspaceText; the file on disk holds diskText. When their line-ending styles
    // differ, the write chokepoint's EOL guard refuses any proposed change built from the workspace text.
    private void SetSource(string workspaceText, string diskText)
    {
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.WriteAllText(path, diskText);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [("TripService.cs", workspaceText, path)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_ValidationFails_FlagWriteRefused_MessageDoesNotClaimFlag()
    {
        var lf = BrokenBridgeSource.Replace("\r\n", "\n");
        SetSource(workspaceText: lf.Replace("\n", "\r\n"), diskText: lf);

        var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

        Assert.That(result.Applied, Is.Empty, "A bridge that fails validation must not be applied.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("Validation produced"));
        Assert.That(message, Does.Not.Contain("flagged NeedsManualReview"),
            "The flag write was refused, so the message must not claim the flag was written.");
        Assert.That(message, Does.Contain("flag could not be written"));
        Assert.That(message, Does.Contain("EolChangeRefused"),
            "The structured refusal code is the only refusal detail that may be surfaced.");
    }

    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_ValidationFails_FlagWriteSucceeds_MessageReportsFlag()
    {
        var lf = BrokenBridgeSource.Replace("\r\n", "\n");
        SetSource(workspaceText: lf, diskText: lf);

        var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

        Assert.That(result.Applied, Is.Empty, "A bridge that fails validation must not be applied.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("Validation produced"));
        Assert.That(message, Does.EndWith("; flagged NeedsManualReview"));
        Assert.That(File.ReadAllText(Path.Combine(_tempDir, "TripService.cs")), Does.Contain("NeedsManualReview"),
            "The flag write succeeded, so the file on disk must carry the NeedsManualReview flag.");
    }
}
