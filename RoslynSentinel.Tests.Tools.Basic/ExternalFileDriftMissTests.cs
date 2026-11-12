using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Miss text of AdminTools: ExternalFileDrift(operation: Acknowledge) with names that match no tracked change, and
// ExternalFileDrift(operation: LedgerFix) with entry ids that match no open ledger entry. The wanted names are ranked against the real
// tracked names / entry ids (NameCandidates.Rank), with a prefilled retry or the no-similar-name fact (plan_not_found_error_orientation,
// Step 5e slice 5). A refused call must leave the tracked list and the ledger exactly as they were.
[TestFixture]
[Category("AdminTools")]
public sealed class ExternalFileDriftMissTests
{
    private const string TrackedPath = "C:\\fake\\OrderService.cs";

    private PersistentWorkspaceManager _manager = null!;
    private AdminTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new AdminTools(_manager);
    }

    [TearDown]
    public void TearDown() => _manager.Dispose();

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("match no tracked change."), "The old sentence ended without facts.");
    }

    // ---- Acknowledge ----

    [Test]
    public void Acknowledge_MisspeltName_OffersTheNearTrackedNameAndClearsNothing()
    {
        _manager.AddExternalChangeForTest(TrackedPath);

        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "OrderServise.cs");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.StartWith("Nothing cleared: 'OrderServise.cs' match no tracked change"));
        Assert.That(message, Does.Contain("Did you mean 'OrderService.cs'? Retry with files: 'OrderService.cs'."));
        Assert.That(message, Does.Contain("Tracked changes: OrderService.cs."));
        Assert.That(message, Does.Contain(ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))), "The List option stays.");
        AssertFactsAndOptionsOnly(message);
        Assert.That(_manager.GetExternalFileChanges(), Is.EqualTo(new[] { TrackedPath }), "A refused Acknowledge must clear nothing.");
    }

    [Test]
    public void Acknowledge_NoSimilarName_StatesTheNoCandidateFactAndKeepsTheListOption()
    {
        _manager.AddExternalChangeForTest(TrackedPath);

        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "Zzz.cs");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No tracked change has a name similar to 'Zzz.cs'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Tracked changes: OrderService.cs."));
        Assert.That(message, Does.Contain(ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))));
        AssertFactsAndOptionsOnly(message);
        Assert.That(_manager.GetExternalFileChanges(), Is.EqualTo(new[] { TrackedPath }), "A refused Acknowledge must clear nothing.");
    }

    // ---- LedgerFix ----

    private static CallSiteLedgerEntry Entry(string entryId, string filePath, int line) => new()
    {
        EntryId = entryId,
        FilePath = filePath,
        Line = line,
        BrokenExpression = "classCLocalVarA.Foo()",
        OldStaticType = "ClassA",
        Status = CallSiteStatus.Ambiguous,
    };

    private static ScopedOperationLedgerEngine OpenLedger()
    {
        var engine = new ScopedOperationLedgerEngine();
        var opened = engine.TryOpen("MoveMember", [Entry("entry-alpha", "C:\\fake\\Caller1.cs", 1), Entry("entry-beta", "C:\\fake\\Caller2.cs", 5)], out var rejection, openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejection);
        return engine;
    }

    [Test]
    public void LedgerFix_MisspeltId_OffersTheNearIdAndChangesNothing()
    {
        var engine = OpenLedger();

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, "entry-alpa");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.StartWith("Nothing changed: unknown entry id(s) entry-alpa"));
        Assert.That(message, Does.Contain("Did you mean 'entry-alpha'? Retry with entryIds: 'entry-alpha'."));
        Assert.That(message, Does.Contain("Unresolved ids: entry-alpha, entry-beta."));
        Assert.That(message, Does.Contain(LedgerMessages.ListCall()), "The LedgerList option stays.");
        AssertFactsAndOptionsOnly(message);
        Assert.That(engine.GetOpenEntries().All(e => !e.IsFixed), Is.True, "A refused LedgerFix must mark nothing fixed.");
        Assert.That(engine.OpenOperationName, Is.EqualTo("MoveMember"), "The ledger stays open.");
    }

    [Test]
    public void LedgerFix_NoSimilarId_StatesTheNoCandidateFactAndKeepsTheListOption()
    {
        var engine = OpenLedger();

        var result = AdminTools.LedgerResult(engine, AdminTools.ExternalFileDriftOperation.LedgerFix, "zzz");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No ledger entry has an id similar to 'zzz'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Unresolved ids: entry-alpha, entry-beta."));
        Assert.That(message, Does.Contain(LedgerMessages.ListCall()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(engine.GetOpenEntries().All(e => !e.IsFixed), Is.True, "A refused LedgerFix must mark nothing fixed.");
    }
}
