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
    public void Resolve_ReturnsTheHedgeUnchanged(string hedge)
    {
        Assert.That(SpeculativeAdvice.Resolve(hedge), Is.EqualTo(hedge));
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
