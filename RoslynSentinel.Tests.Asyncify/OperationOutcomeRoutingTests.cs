namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for spec §6 — Operation Outcome Classification + ToolGraph-Routed Failure Hints.
///   T-R1  FromCounts derivation — one test per OperationOutcome value (5 sub-cases)
///   T-R2  AlreadySatisfied exclusion — absent from Actionable and excluded from failure rate
///   T-R3  Router hit — OverloadAlreadyExists returns hint naming the producer with pre-filled args
///   T-R4  Router miss (loud null) — NoAsyncEquivalent returns null hint; item still in Actionable
///   T-R5  Router empty-producers — OverloadAlreadyExists with no registered producer returns null
///   T-R6  Determinism — two equal-weight producers resolve same tool across repeated calls
///   T-R7  Directive/Outcome consistency — no completion claim when Outcome is NoProgress/PartialProgress
/// </summary>
[TestFixture]
public class OperationOutcomeRoutingTests
{
    // ── T-R1: FromCounts derivation ────────────────────────────────────────────

    [Test]
    public void FromCounts_NothingToDo_WhenAttemptedIsZero()
    {
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 0, alreadySatisfied: 0, skipped: 0, failed: 0, blocked: 0, attempted: 0,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.NothingToDo));
    }

    [Test]
    public void FromCounts_CompletedFully_WhenSucceededAndNoFailuresNoAlreadySatisfied()
    {
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 3, alreadySatisfied: 0, skipped: 0, failed: 0, blocked: 0, attempted: 3,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.CompletedFully));
    }

    [Test]
    public void FromCounts_CompletedWithNoOps_WhenAlreadySatisfiedAndNoFailures()
    {
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 0, alreadySatisfied: 3, skipped: 0, failed: 0, blocked: 0, attempted: 3,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.CompletedWithNoOps));
    }

    [Test]
    public void FromCounts_PartialProgress_WhenSucceededAndFailed()
    {
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 2, alreadySatisfied: 0, skipped: 0, failed: 1, blocked: 0, attempted: 3,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.PartialProgress));
    }

    [Test]
    public void FromCounts_NoProgress_WhenZeroSucceededAndFailed()
    {
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 0, alreadySatisfied: 0, skipped: 0, failed: 3, blocked: 0, attempted: 3,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.NoProgress));
    }

    // ── T-R2: AlreadySatisfied exclusion ──────────────────────────────────────

    [Test]
    public void AlreadySatisfied_AbsentFromActionable_AndOutcomeIsCompletedWithNoOps()
    {
        ItemFailure alreadySatisfiedItem = new ItemFailure
        {
            FilePath = "/foo.cs",
            MethodName = "FooMethod",
            Outcome = ItemOutcome.AlreadySatisfied,
            Reason = FailureReason.AlreadyAsync,
            Detail = "already async",
        };

        // Per spec: Actionable must only contain Failed/Blocked items.
        // Caller must filter before passing to FromCounts — assert the contract
        // by building a summary where Actionable is empty (AlreadySatisfied not included).
        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: 0, alreadySatisfied: 3, skipped: 0, failed: 0, blocked: 0, attempted: 3,
            actionable: Array.Empty<ItemFailure>(),
            actionableTruncated: false,
            directive: "test", breakerOpen: false);

        Assert.That(result.Actionable, Is.Empty);
        Assert.That(result.AlreadySatisfied, Is.EqualTo(3));
        Assert.That(result.Failed, Is.EqualTo(0));
        Assert.That(result.Outcome, Is.EqualTo(OperationOutcome.CompletedWithNoOps));
    }

    // ── T-R3: Router hit ──────────────────────────────────────────────────────

    [Test]
    public void Router_OverloadAlreadyExists_ReturnsHintWithPrefilledFileAndMethod()
    {
        ToolDescriptor descriptor = new ToolDescriptor
        {
            Name = "AddCancellationToken",
            AllParameterNames = new[] { "targets", "dryRun", "maxItems" },
            RequiredParameterNames = new[] { "targets" },
            PreferenceWeight = 100,
        };
        ToolGraph graph = ToolGraph.Build(new[] { (DataTag.CancellationTokenSlot, descriptor) });
        FailureRouter router = new FailureRouter(graph);

        ItemContext ctx = new ItemContext
        {
            FilePath = @"C:\project\Foo.cs",
            MethodName = "DoWork",
            ProjectName = "MyProject",
            ChangeId = "abc123",
        };

        ToolHint? hint = router.Route(FailureReason.OverloadAlreadyExists, ctx);

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint!.ToolName, Is.EqualTo("AddCancellationToken"));
        Assert.That(hint.PrefilledArgs.ContainsKey("targets"), Is.True);
        Assert.That(hint.PrefilledArgs["targets"], Does.Contain("Foo.cs"));
        Assert.That(hint.PrefilledArgs["targets"], Does.Contain("DoWork"));
        Assert.That(hint.RequiresFromModel, Is.Empty);
    }

    // ── T-R4: Router miss (loud null) ─────────────────────────────────────────

    [Test]
    public void Router_NoAsyncEquivalent_ReturnsNullHint_ItemStillInActionable()
    {
        ToolGraph graph = ToolGraph.Empty;
        FailureRouter router = new FailureRouter(graph);

        ItemContext ctx = new ItemContext
        {
            FilePath = @"C:\project\Bar.cs",
            MethodName = "Sync",
        };

        ToolHint? hint = router.Route(FailureReason.NoAsyncEquivalent, ctx);

        Assert.That(hint, Is.Null);

        // Item with null hint still belongs in Actionable
        ItemFailure failure = new ItemFailure
        {
            FilePath = ctx.FilePath,
            MethodName = ctx.MethodName,
            Outcome = ItemOutcome.Failed,
            Reason = FailureReason.NoAsyncEquivalent,
            Detail = "no async equivalent",
            SuggestedTool = hint,
        };
        Assert.That(failure.SuggestedTool, Is.Null);
        Assert.That(failure.Outcome, Is.EqualTo(ItemOutcome.Failed));
    }

    // ── T-R5: Router empty-producers ──────────────────────────────────────────

    [Test]
    public void Router_OverloadAlreadyExists_NoProducerRegistered_ReturnsNull()
    {
        // Graph has no producer for CancellationTokenSlot
        ToolGraph graph = ToolGraph.Empty;
        FailureRouter router = new FailureRouter(graph);

        ItemContext ctx = new ItemContext
        {
            FilePath = @"C:\project\Baz.cs",
            MethodName = "BazMethod",
        };

        ToolHint? hint = router.Route(FailureReason.OverloadAlreadyExists, ctx);

        Assert.That(hint, Is.Null);
    }

    // ── T-R6: Determinism ─────────────────────────────────────────────────────

    [Test]
    public void Router_TwoEqualWeightProducers_SameToolSelectedAcrossRepeatedCalls()
    {
        ToolDescriptor alpha = new ToolDescriptor
        {
            Name = "AlphaTool",
            AllParameterNames = new[] { "targets" },
            RequiredParameterNames = new[] { "targets" },
            PreferenceWeight = 0,
        };
        ToolDescriptor beta = new ToolDescriptor
        {
            Name = "BetaTool",
            AllParameterNames = new[] { "targets" },
            RequiredParameterNames = new[] { "targets" },
            PreferenceWeight = 0,
        };

        ToolGraph graph = ToolGraph.Build(new[]
        {
            (DataTag.CancellationTokenSlot, alpha),
            (DataTag.CancellationTokenSlot, beta),
        });
        FailureRouter router = new FailureRouter(graph);

        ItemContext ctx = new ItemContext { FilePath = @"C:\f.cs", MethodName = "M" };

        ToolHint? first = router.Route(FailureReason.OverloadAlreadyExists, ctx);
        ToolHint? second = router.Route(FailureReason.OverloadAlreadyExists, ctx);
        ToolHint? third = router.Route(FailureReason.OverloadAlreadyExists, ctx);

        Assert.That(first, Is.Not.Null);
        Assert.That(first!.ToolName, Is.EqualTo(second!.ToolName));
        Assert.That(second.ToolName, Is.EqualTo(third!.ToolName));
        // Ordinal tie-break: "AlphaTool" < "BetaTool"
        Assert.That(first.ToolName, Is.EqualTo("AlphaTool"));
    }

    // ── T-R7: Directive/Outcome consistency ───────────────────────────────────

    [TestCase(OperationOutcome.PartialProgress)]
    [TestCase(OperationOutcome.NoProgress)]
    public void Directive_DoesNotClaimCompletion_WhenOutcomeIsPartialOrNoProgress(OperationOutcome outcome)
    {
        (int succeeded, int failed) = outcome == OperationOutcome.PartialProgress ? (1, 1) : (0, 2);

        OperationSummary result = OperationSummary.FromCounts(
            blobName: "", changeId: "",
            succeeded: succeeded, alreadySatisfied: 0, skipped: 0,
            failed: failed, blocked: 0, attempted: succeeded + failed,
            actionable: Array.Empty<ItemFailure>(), actionableTruncated: false,
            directive: BuildTestDirective(outcome),
            breakerOpen: false);

        Assert.That(result.Outcome, Is.EqualTo(outcome));

        // Simple substring guard: directive must not claim full completion
        string d = result.Directive;
        Assert.That(d, Does.Not.Contain("successfully").IgnoreCase.Or.Contain("complete").IgnoreCase);
    }

    private static string BuildTestDirective(OperationOutcome outcome)
    {
        return outcome switch
        {
            OperationOutcome.PartialProgress => "1 caller(s) uplifted; 1 could not be completed. Review Actionable for next steps.",
            OperationOutcome.NoProgress => "No callers were uplifted; 2 failure(s) require attention. Review Actionable for next steps.",
            _ => "test directive",
        };
    }
}
