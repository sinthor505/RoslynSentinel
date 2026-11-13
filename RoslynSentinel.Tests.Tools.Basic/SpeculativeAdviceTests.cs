using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice E step 1 of proposal_not_found_cause_from_prior_tool_call: <see cref="SpeculativeAdvice.Resolve"/> is the identity
/// function, and the not-found builders that route their hedge sentence through it render byte-identical text. The tail
/// assertions pin the exact hedge sentences, so a builder that rewords one fails here.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class SpeculativeAdviceTests
{
    private static readonly string MissingPath = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz", "OrderPricingCalculater.cs");

    [TestCase("")]
    [TestCase("A file read earlier under this name may since have been renamed or moved.")]
    [TestCase("If none of these is the symbol you meant, it does not exist in this solution.")]
    [TestCase("first line\nsecond line\r\nthird line")]
    [TestCase("  leading and trailing space  ")]
    public void Resolve_WithNoContext_ReturnsTheHedgeUnchanged(string hedge)
    {
        ToolCallContext.Set(null);

        Assert.That(SpeculativeAdvice.Resolve(hedge), Is.EqualTo(hedge));
    }

    private const string Hedge = "A file read earlier under this name may since have been renamed or moved.";

    [TearDown]
    public void ResetContext()
    {
        ToolCallContext.Set(null);
    }

    // Records the given effects as one writer call (session writerSession) and attaches a lookup context for term (session lookupSession).
    private static void AttachLookup(string term, string? lookupSession, string? writerSession, Action<ToolCallContext>? record, bool withIndex = true)
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("agent-1", null, "RenameSymbol", "rename", 0) { SessionId = writerSession };
        writer.AssignToolCallId("a76e9f1c");
        record?.Invoke(writer);
        writer.CommitPendingEffects(index);
        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0)
        {
            SessionId = lookupSession,
            MissingTerm = term,
            EditEffects = withIndex ? index : null,
        });
    }

    private static void RecordRename(ToolCallContext writer)
    {
        writer.RecordEffect(EditEffectKind.Renamed, "N.Foo.Bar", "M:N.Foo.Bar", "Bar", "Baz", null, "Foo.cs");
    }

    [Test]
    public void Resolve_WithARecordedRename_ReturnsTheFactInPlaceOfTheHedge()
    {
        AttachLookup("Bar", "s1", "s1", RecordRename);

        var text = SpeculativeAdvice.Resolve(Hedge);

        Assert.That(text, Is.Not.EqualTo(Hedge));
        Assert.That(text, Does.StartWith("Call `a76e9f1c` (`RenameSymbol`, this agent, "));
        Assert.That(text, Does.EndWith(" ago) renamed `N.Foo.Bar` to `N.Foo.Baz`."));
        Assert.That(text, Does.Not.StartWith("`Bar` was not found"));
        Assert.That(text, Does.Not.Contain(Hedge));
    }

    [Test]
    public void Resolve_WithNoEffectForTheTerm_ReturnsTheHedge()
    {
        AttachLookup("Unrelated", "s1", "s1", RecordRename);

        Assert.That(SpeculativeAdvice.Resolve(Hedge), Is.EqualTo(Hedge));
    }

    [Test]
    public void Resolve_WithAnotherSessionsEffect_ReturnsTheHedge()
    {
        AttachLookup("Bar", "s2", "s1", RecordRename);

        Assert.That(SpeculativeAdvice.Resolve(Hedge), Is.EqualTo(Hedge));
    }

    [Test]
    public void Resolve_WhenTheNameWasDeclaredAgainAfterTheRename_ReturnsTheHedge()
    {
        AttachLookup("Bar", "s1", "s1", writer =>
        {
            RecordRename(writer);
            writer.RecordEffect(EditEffectKind.Added, "N.Foo.Bar", "M:N.Foo.Bar", "Bar", null, null, "Foo.cs");
        });

        Assert.That(SpeculativeAdvice.Resolve(Hedge), Is.EqualTo(Hedge));
    }

    [Test]
    public void Resolve_WithoutAnEffectIndexOrATerm_ReturnsTheHedge()
    {
        AttachLookup("Bar", "s1", "s1", RecordRename, withIndex: false);
        Assert.That(SpeculativeAdvice.Resolve(Hedge), Is.EqualTo(Hedge));

        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0) { SessionId = "s1", EditEffects = new EditEffectIndex() });
        Assert.That(SpeculativeAdvice.Resolve(Hedge), Is.EqualTo(Hedge));
    }

    [Test]
    public void Resolve_WhenTheSolutionAccessorThrows_StillReturnsTheFact()
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("agent-1", null, "RenameSymbol", "rename", 0) { SessionId = "s1" };
        RecordRename(writer);
        writer.CommitPendingEffects(index);
        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0)
        {
            SessionId = "s1",
            MissingTerm = "Bar",
            EditEffects = index,
            CurrentSolution = () => throw new InvalidOperationException("workspace busy"),
        });

        Assert.That(SpeculativeAdvice.Resolve(Hedge), Does.EndWith("renamed `N.Foo.Bar` to `N.Foo.Baz`."));
    }

    [Test]
    public void FileNotFoundMessage_WithCandidates_KeepsTheRetrySentenceAndHedgeVerbatim()
    {
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate("OrderPricingCalculator.cs", 1, false)];

        var text = FileNotFoundMessage.Build(MissingPath, candidates, 2);

        Assert.That(text, Does.EndWith("\nRetry with the exact path of the file you meant. If none of these is the file you meant, it does not exist in this solution."));
        Assert.That(text, Does.Contain("Did you mean one of these?\n  - OrderPricingCalculator.cs"));
    }

    [Test]
    public void FileNotFoundMessage_WithoutCandidatesAndNoListingTool_EndsWithTheRenamedOrMovedHedgeVerbatim()
    {
        IReadOnlyList<FileCandidate> noCandidates = [];

        var text = FileNotFoundMessage.Build(MissingPath, noCandidates, 3, name => name != "ListSolutionItems");

        Assert.That(text, Does.EndWith("No file in the solution has a similar name. A file read earlier under this name may since have been renamed or moved."));
    }

    [Test]
    public void SymbolNotFoundDetail_WithCandidates_EndsWithTheDoesNotExistHedgeVerbatim()
    {
        IReadOnlyList<SymbolCandidate> candidates = [new SymbolCandidate("CalcDiscount", "method", "Calculator", "Calc.cs", 12, null, 1)];

        var text = SymbolNotFoundDetail.RenderCandidates("CalcDsc", "method", "the loaded solution", candidates);

        Assert.That(text, Does.EndWith("\nRetry with the exact symbol you meant. If none of these is the symbol you meant, it does not exist in this solution."));
    }

    [Test]
    public void SymbolNotFoundDetail_WithoutCandidates_EndsWithTheDoesNotExistHedgeVerbatim()
    {
        var text = SymbolNotFoundDetail.RenderNoCandidates("Missing", "method", "the loaded solution", null, null);

        Assert.That(text, Does.EndWith(". If it is not in that list, the symbol does not exist in this solution."));
    }
}
