// OperationBlobWriter's filename sanitization and typed failure result.
//
// The A3 defect from run 20260910-013550-398: 17 call sites in SentinelAdvancedRefactoringTools
// passed slashed operation names ("WrapRange/region", "Inline/method", …). Path.Combine resolved
// the blob into a non-existent operations/WrapRange/ subdirectory, the write threw
// DirectoryNotFoundException, and the catch swallowed it into a return string nobody inspected —
// so WrapRange, ExtractMembers, SyncInterface, Inline and MoveType all returned changeIds
// UndoLastApply could never resolve. The call sites were renamed, but sanitization here is the
// durable fix: it holds for any future name regardless of caller discipline. These tests pin the
// sink, so a reverted rename can no longer reintroduce the bug.

namespace RoslynSentinel.Tests;

[TestFixture]
public class OperationBlobWriterTests
{
    private string _solutionRoot = null!;

    [SetUp]
    public void Setup()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "OperationBlobWriterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_solutionRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_solutionRoot))
        {
            Directory.Delete(_solutionRoot, recursive: true);
        }
    }

    private static List<OperationItemRecord> OneItem() =>
    [
        new OperationItemRecord
        {
            FilePath = "C:/fake/File.cs",
            Outcome = ItemRecordOutcome.Succeeded,
            BeforeSource = "// before",
        }
    ];

    [Test]
    public async Task WriteAsync_SlashedToolName_StillProducesAFindableBlobAsync()
    {
        // The regression, stated directly. Before sanitization this threw
        // DirectoryNotFoundException and FindBlobPath returned null.
        const string changeId = "abc12345";

        var result = await OperationBlobWriter.WriteAsync(
            "WrapRange/region", changeId, OneItem(), _solutionRoot);

        Assert.That(result.Written, Is.True, result.Diagnostic);
        Assert.That(OperationBlobWriter.FindBlobPath(changeId, _solutionRoot), Is.Not.Null,
            "a changeId that was issued must be resolvable by UndoLastApply");
    }

    [Test]
    public async Task WriteAsync_SlashedToolName_FlattensTheSlashRatherThanNestingAsync()
    {
        await OperationBlobWriter.WriteAsync("Inline/method", "def67890", OneItem(), _solutionRoot);

        var operationsDir = Path.Combine(_solutionRoot, ".roslynsentinel", "operations");
        Assert.That(Directory.GetDirectories(operationsDir), Is.Empty,
            "the operation name must not create a subdirectory — that nesting was the root cause");
        Assert.That(Path.GetFileName(Directory.GetFiles(operationsDir).Single()),
            Does.StartWith("Inline_method_"));
    }

    [TestCase("Weird:Name")]
    [TestCase("back\\slash")]
    [TestCase("with*star")]
    public async Task WriteAsync_OtherInvalidFileNameChars_AreAlsoSanitizedAsync(string toolName)
    {
        // Sanitization covers Path.GetInvalidFileNameChars() as a set, not just the two separators,
        // so a future operation name can't find a different way to break the same invariant.
        var changeId = Guid.NewGuid().ToString("n")[..8];

        var result = await OperationBlobWriter.WriteAsync(toolName, changeId, OneItem(), _solutionRoot);

        Assert.That(result.Written, Is.True, result.Diagnostic);
        Assert.That(OperationBlobWriter.FindBlobPath(changeId, _solutionRoot), Is.Not.Null);
    }

    [Test]
    public async Task WriteAsync_NoSolutionRoot_IsNotAnIntegrityFailureAsync()
    {
        // With no solution root there is nowhere a blob could live and no undo semantics at all —
        // the in-memory/test-solution case, not a server fault. Classifying it as a failure tripped
        // the unrecoverable breaker on the first apply of every SetTestSolution-based fixture and
        // then refused all subsequent ones, so this distinction is load-bearing.
        var result = await OperationBlobWriter.WriteAsync("Member", "aaa11111", OneItem(), solutionRoot: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.False);
            Assert.That(result.IsIntegrityFailure, Is.False);
            Assert.That(result.Diagnostic, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task WriteAsync_SolutionRootPresentButUnwritable_IsAnIntegrityFailureAsync()
    {
        // The case that must still halt: a root exists, so a blob is genuinely owed, but the write
        // cannot land. "Owed but absent" has to stay distinguishable from "not owed" — a caller
        // that can't tell them apart either warns on every no-op or stays silent on every fault.
        // A file standing where the operations directory must go makes CreateDirectory throw.
        var blocker = Path.Combine(_solutionRoot, ".roslynsentinel");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var result = await OperationBlobWriter.WriteAsync("Member", "eee55555", OneItem(), _solutionRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.True);
            Assert.That(result.IsIntegrityFailure, Is.True);
            Assert.That(result.Diagnostic, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task WriteApplyBlobAsync_NothingWritten_IsNotNeededRatherThanFailedAsync()
    {
        var result = await OperationBlobWriter.WriteApplyBlobAsync(
            "Member", "bbb22222",
            new ApplyChangesResult(Success: true, SucceededFiles: [], FailedFiles: new(), Summary: "no-op"),
            _solutionRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.False, "no files were written, so no blob is owed");
            Assert.That(result.IsIntegrityFailure, Is.False, "a no-op must not read as a fault");
        });
    }

    [Test]
    public async Task WriteBatchBlobOrTripAsync_OnFailure_TripsTheBreakerAsync()
    {
        // The batch path (Asyncify, BulkComment, …). Their result shape carries a BlobName string
        // rather than an ApplyOutcome, and all ten call sites previously assigned the write's
        // return value straight into it without checking — hence the centralized helper.
        var breaker = new RecordingBreaker();
        // Unwritable root, not a missing one: a missing root means no blob is owed at all.
        await File.WriteAllTextAsync(Path.Combine(_solutionRoot, ".roslynsentinel"), "not a directory");

        var blobName = await OperationBlobWriter.WriteBatchBlobOrTripAsync(
            breaker, "asyncify", "ccc33333", OneItem(), _solutionRoot);

        Assert.That(breaker.Tripped, Is.True, "a failed batch blob write must halt the session");
        Assert.That(blobName, Does.Contain("blob"), "the diagnostic should still reach the result");
    }

    [Test]
    public async Task WriteBatchBlobOrTripAsync_OnSuccess_DoesNotTripAsync()
    {
        var breaker = new RecordingBreaker();

        var blobName = await OperationBlobWriter.WriteBatchBlobOrTripAsync(
            breaker, "asyncify", "ddd44444", OneItem(), _solutionRoot);

        Assert.That(breaker.Tripped, Is.False);
        Assert.That(blobName, Does.StartWith("asyncify_"));
    }

    /// <summary>
    /// Minimal <see cref="IUnrecoverableBreaker"/> that just records whether it was tripped.
    /// Used instead of a real workspace manager because these tests are about the writer's
    /// decision to trip, not about the workspace manager's halt state.
    /// </summary>
    private sealed class RecordingBreaker : IUnrecoverableBreaker
    {
        public bool Tripped { get; private set; }
        public string? Message { get; private set; }

        public void Trip(string toolName, string changeId, string diagnostic)
        {
            Tripped = true;
            Message = $"{toolName}/{changeId}: {diagnostic}";
        }

        bool IUnrecoverableBreaker.IsTripped() => Tripped;
        string? IUnrecoverableBreaker.StateMessage() => Message;
        bool ICircuitBreaker.IsTripped() => Tripped;
        string? ICircuitBreaker.StateMessage() => Message;
    }
}
