// IScopedOperationLedger.IsBlocked -> the per-file refusal wired into ApplyProposedChangesAsync
// (see docs/current/proposal_scoped_operation_ledger.md, Decision 2). While a ledger is open, only
// its own tracked files (where RecordFix's resolving edits must land) stay writable; every other
// file is refused until every entry is resolved. Unlike IUnrecoverableBreaker, this returns a
// failed ApplyChangesResult rather than throwing SessionHaltedException.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ScopedOperationLedgerBlockingTests
{
    private TestSolutionFixture _fixture;
    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public async Task SetUpAsync()
    {
        _fixture = new TestSolutionFixture();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    private IScopedOperationLedger Ledger => _workspaceManager;

    [Test]
    public async Task OpenLedgerEntry_AllowsWriteToItsOwnTrackedFileAsync()
    {
        // The ledger's own tracked file is exactly where RecordFix's resolving edit must land ->
        // IsBlocked deliberately lets writes to that file through while entries remain unresolved.
        var targetFile = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .First(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase));
        var original = await File.ReadAllTextAsync(targetFile);
        var filePath = _workspaceManager.SetFilePath(targetFile);

        var opened = Ledger.TryOpen(
            "MoveMember_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = filePath.Absolute,
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out var rejectionReason);
        Assert.That(opened, Is.True, rejectionReason);

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [filePath] = original + "\n// appended by test\n"
        };

        var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false);

        Assert.That(result.Success, Is.True, result.Summary);
    }

    [Test]
    public async Task NoOpenLedger_WritesStillSucceedAsync()
    {
        // Guards against the ledger blocking by default -> a false positive here would disable
        // every mutating tool on a healthy server with no ledger ever opened.
        var targetFile = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .First(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase));
        var original = await File.ReadAllTextAsync(targetFile);

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [_workspaceManager.SetFilePath(targetFile)] = original + "\n// appended by test\n"
        };

        var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false);

        Assert.That(result.Success, Is.True, result.Summary);
    }

    [Test]
    public async Task OpenLedgerEntry_RefusesWriteToUnrelatedFileAsync()
    {
        // A file the open ledger has no entry for is exactly the "unrelated change" the ledger
        // exists to hold back until every tracked entry is resolved.
        var files = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        var trackedFile = files[0];
        var unrelatedFile = files[1];
        var unrelatedOriginal = await File.ReadAllTextAsync(unrelatedFile);
        var trackedPath = _workspaceManager.SetFilePath(trackedFile);
        var unrelatedPath = _workspaceManager.SetFilePath(unrelatedFile);

        Ledger.TryOpen(
            "MoveMember_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = trackedPath.Absolute,
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out _);

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [unrelatedPath] = unrelatedOriginal + "\n// appended by test\n"
        };

        var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Summary, Does.Contain("scoped operation ledger"));
        });
        Assert.That(await File.ReadAllTextAsync(unrelatedFile), Is.EqualTo(unrelatedOriginal),
            "the refused write must not have reached disk");
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public void RecordUndo_OfFixChangeId_ReTripsBreakerAfterRelease()
    {
        // Resolve the only entry (breaker releases), then undo that fix's own changeId ->
        // the entry goes back to unresolved and the ledger must be open (blocking) again.
        var opened = Ledger.TryOpen(
            "MoveMember_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = "C:\\fake\\Caller.cs",
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out var rejectionReason,
            openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejectionReason);

        Ledger.RecordFix(["entry-1"], "fix-change-id");
        Assert.That(Ledger.TryRelease(), Is.True, "all entries fixed -> release should succeed");

        // TryRelease cleared the ledger, so there's nothing left to undo against - reopen to
        // exercise the re-trip in a state where the ledger is actually still open.
        Ledger.TryOpen(
            "MoveMember_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = "C:\\fake\\Caller.cs",
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out _,
            openingChangeId: "move-change-id-2");
        Ledger.RecordFix(["entry-1"], "fix-change-id-2");
        Assert.That(Ledger.GetOpenEntries().Single().IsFixed, Is.True);

        Ledger.RecordUndo("fix-change-id-2");

        Assert.That(Ledger.GetOpenEntries().Single().IsFixed, Is.False,
            "undoing the fix's own changeId must flip the entry back to unresolved");
        Assert.That(Ledger.TryRelease(), Is.False, "an unresolved entry must keep the breaker tripped");
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public void RecordUndo_OfOpeningChangeId_InvalidatesEveryEntryEvenIfAlreadyFixed()
    {
        // Undoing the move that created the ledger (not one of its per-entry fixes) invalidates
        // the whole ledger's worth of tracking: every entry - including ones already individually
        // fixed - goes back to unresolved, since the operation they were tracking no longer exists.
        var opened = Ledger.TryOpen(
            "MoveMember_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = "C:\\fake\\Caller1.cs",
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                },
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-2",
                    FilePath = "C:\\fake\\Caller2.cs",
                    Line = 5,
                    BrokenExpression = "classCLocalVarB.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out var rejectionReason,
            openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejectionReason);

        Ledger.RecordFix(["entry-1"], "fix-change-id");
        var entries = Ledger.GetOpenEntries();
        Assert.That(entries.Single(e => e.EntryId == "entry-1").IsFixed, Is.True);
        Assert.That(entries.Single(e => e.EntryId == "entry-2").IsFixed, Is.False);

        Ledger.RecordUndo("move-change-id");

        var afterUndo = Ledger.GetOpenEntries();
        Assert.Multiple(() =>
        {
            Assert.That(afterUndo.Single(e => e.EntryId == "entry-1").IsFixed, Is.False,
                "even the already-fixed entry must be invalidated when the opening move is undone");
            Assert.That(afterUndo.Single(e => e.EntryId == "entry-2").IsFixed, Is.False);
        });
        Assert.That(Ledger.TryRelease(), Is.False, "invalidated entries must keep the breaker tripped");
    }
}
