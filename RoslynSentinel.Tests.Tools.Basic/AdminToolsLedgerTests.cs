// AdminTools -> ExternalFileDrift LedgerList / LedgerFix / LedgerRelease over a scoped operation ledger,
// plus the engine-level ForceRelease contract they depend on. The end-to-end refusal of unrelated writes
// lives in ScopedOperationLedgerRefusalTests (Tests.Battery.Basic) because it needs a real solution on disk.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
public class AdminToolsLedgerTests
{
    private const string FileA = "C:\\fake\\Caller1.cs";
    private const string FileB = "C:\\fake\\Caller2.cs";

    private IWorkspaceManager _workspaceManager;
    private AdminTools _tools;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new AdminTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static CallSiteLedgerEntry Entry(string entryId, string filePath, int line) => new()
    {
        EntryId = entryId,
        FilePath = filePath,
        Line = line,
        BrokenExpression = "classCLocalVarA.Foo()",
        OldStaticType = "ClassA",
        Status = CallSiteStatus.Ambiguous,
    };

    // Opens a MoveMember ledger with entry-1 (fixed) and entry-2 (unresolved) on the given ledger.
    private static void OpenTwoEntryLedger(IScopedOperationLedger ledger)
    {
        var opened = ledger.TryOpen(
            "MoveMember",
            [Entry("entry-1", FileA, 1), Entry("entry-2", FileB, 5)],
            out var rejectionReason,
            openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejectionReason);
        ledger.RecordFix(["entry-1"], "fix-change-id");
    }

    // ---- LedgerList ----

    [Test]
    public void LedgerList_NoLedgerOpen_SaysNoneOpen()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.LedgerList);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Does.StartWith("Ledger: none open"));
    }

    [Test]
    public void LedgerList_OpenLedger_NamesOperationEntriesLocationsAndCallText()
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerList, null);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Ledger open for 'MoveMember'"));
            Assert.That(text, Does.Contain("opened by change move-change-id"));
            Assert.That(text, Does.Contain("2 entries, 1 unresolved"));
            Assert.That(text, Does.Contain("entry-1"));
            Assert.That(text, Does.Contain("entry-2"));
            Assert.That(text, Does.Contain($"{FileA}:1"));
            Assert.That(text, Does.Contain($"{FileB}:5"));
            Assert.That(text, Does.Contain(LedgerMessages.FixCall("entry-2")));
            Assert.That(text, Does.Contain(LedgerMessages.ReleaseCall()));
        });
        // Unresolved entries come first so a cap never hides the ones still blocking writes.
        Assert.That(text.IndexOf("entry-2", StringComparison.Ordinal), Is.LessThan(text.IndexOf("entry-1", StringComparison.Ordinal)));
    }

    [Test]
    public void LedgerList_NullLedger_IsInvalidArgumentRefusal()
    {
        var result = AdminTools.LedgerResult(null, AdminTools.ExternalFileDriftOperation.LedgerList, null);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("nothing changed"));
    }

    // ---- LedgerFix ----

    [Test]
    public void LedgerFix_UnknownId_IsNotFoundAndChangesNothing()
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, "no-such-entry");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("no-such-entry"));
        Assert.That(result.ErrorData!.Message, Does.Contain("Nothing changed"));
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetOpenEntries().Single(e => e.EntryId == "entry-2").IsFixed, Is.False);
            Assert.That(engine.OpenOperationName, Is.EqualTo("MoveMember"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void LedgerFix_EmptyEntryIds_IsInvalidArgument(string? entryIds)
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, entryIds);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("needs entryIds"));
        Assert.That(engine.GetOpenEntries().Single(e => e.EntryId == "entry-2").IsFixed, Is.False);
    }

    [Test]
    public void LedgerFix_OneOfTwoRemaining_LeavesLedgerOpen()
    {
        // Both entries start unresolved, so fixing one must leave the other still blocking.
        var engine = new ScopedOperationLedgerEngine();
        var opened = engine.TryOpen(
            "MoveMember",
            [Entry("entry-1", FileA, 1), Entry("entry-2", FileB, 5)],
            out var rejectionReason,
            openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejectionReason);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, "entry-2");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Does.Contain("1 unresolved remain; ledger still open"));
        Assert.Multiple(() =>
        {
            Assert.That(engine.OpenOperationName, Is.EqualTo("MoveMember"));
            Assert.That(engine.GetOpenEntries().Single(e => e.EntryId == "entry-2").IsFixed, Is.True);
            Assert.That(engine.GetOpenEntries().Single(e => e.EntryId == "entry-1").IsFixed, Is.False);
        });
    }

    [Test]
    public void LedgerFix_LastUnresolvedEntry_ReleasesLedgerSoANewOneCanOpen()
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, "entry-2");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Does.Contain("ledger released"));
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetOpenEntries(), Is.Empty);
            Assert.That(engine.OpenOperationName, Is.Null);
        });

        var reopened = engine.TryOpen("MoveMember", [Entry("entry-3", FileA, 9)], out var rejectionReason);
        Assert.That(reopened, Is.True, rejectionReason);
    }

    // ---- LedgerRelease ----

    [Test]
    public void LedgerRelease_WithUnresolvedEntries_AbandonsAndSaysHowMany()
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerRelease, null);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Does.Contain("1 unresolved entry(ies) were abandoned"));
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetOpenEntries(), Is.Empty);
            Assert.That(engine.OpenOperationName, Is.Null);
        });
    }

    [Test]
    public void LedgerRelease_NoLedgerOpen_IsOkAndChangesNothing()
    {
        var engine = new ScopedOperationLedgerEngine();

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerRelease, null);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Is.EqualTo("Ledger: none open; nothing changed."));
        Assert.That(engine.OpenOperationName, Is.Null);
    }

    // ---- entryIds is LedgerFix-only ----

    [TestCase(AdminTools.ExternalFileDriftOperation.Status)]
    [TestCase(AdminTools.ExternalFileDriftOperation.List)]
    [TestCase(AdminTools.ExternalFileDriftOperation.Acknowledge)]
    [TestCase(AdminTools.ExternalFileDriftOperation.LedgerList)]
    [TestCase(AdminTools.ExternalFileDriftOperation.LedgerRelease)]
    public void EntryIds_OnNonLedgerFixOperation_IsInvalidArgumentRefusal(AdminTools.ExternalFileDriftOperation operation)
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: operation, entryIds: "entry-1");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("entryIds is only used with operation: LedgerFix"));
        Assert.That(result.ErrorData!.Message, Does.Contain("nothing changed"));
    }

    // ---- ForceRelease (engine contract used by LedgerRelease) ----

    [Test]
    public void ForceRelease_WithUnresolvedEntries_ReturnsUnresolvedCountAndClearsOpeningFields()
    {
        var engine = new ScopedOperationLedgerEngine();
        OpenTwoEntryLedger(engine);
        Assert.That(engine.OpeningChangeId, Is.EqualTo("move-change-id"));

        var abandoned = engine.ForceRelease();

        Assert.Multiple(() =>
        {
            Assert.That(abandoned, Is.EqualTo(1), "only entry-2 was unresolved");
            Assert.That(engine.OpenOperationName, Is.Null);
            Assert.That(engine.OpeningChangeId, Is.Null);
            Assert.That(engine.GetOpenEntries(), Is.Empty);
        });
    }

    [Test]
    public void ForceRelease_NothingOpen_ReturnsZero()
    {
        var engine = new ScopedOperationLedgerEngine();

        Assert.That(engine.ForceRelease(), Is.EqualTo(0));
    }
}
