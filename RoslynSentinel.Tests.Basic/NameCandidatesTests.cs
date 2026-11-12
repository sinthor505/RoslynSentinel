using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// NameCandidates is the "did you mean" similarity rule used by not-found errors. These tests pin the ranking (nearest
/// first, ties in input order), the cap, case-insensitive matching, the noise guard that must return nothing for unrelated
/// names, and the 3-character minimum for the wanted name.
/// </summary>
[TestFixture]
[Category("NameCandidates")]
public class NameCandidatesTests
{
    private static async Task<List<string>> FixtureMethodNamesAsync()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(OrderPricingFixture.FileName, OrderPricingFixture.Source)]);
        var document = solution.Projects.Single().Documents.Single();
        var root = await document.GetSyntaxRootAsync();
        return root!.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
    }

    [Test]
    public async Task Rank_TypoAgainstFixtureMethods_ReturnsCalcDiscOnlyAsync()
    {
        var names = await FixtureMethodNamesAsync();

        var result = NameCandidates.Rank("CalcDsc", names, n => n);

        Assert.That(names, Is.EquivalentTo(new[] { "CalcDisc", "OrderCheckout", "GetTotal" }));
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Item, Is.EqualTo("CalcDisc"));
        Assert.That(result[0].Distance, Is.EqualTo(1));
    }

    [Test]
    public async Task Rank_UnrelatedNameAgainstFixtureMethods_ReturnsEmptyAsync()
    {
        var names = await FixtureMethodNamesAsync();

        var result = NameCandidates.Rank("ApplyLoyaltyBonus", names, n => n);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Rank_UnrelatedNameAgainstTypeStems_ReturnsEmpty()
    {
        var stems = new[] { "OrderPricingCalculator", "OrderLine", "CustomerRepository" };

        var result = NameCandidates.Rank("ShippingLabelPrinter", stems, n => n);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Rank_NearestFirst_ExactMatchBeforeNearMisses()
    {
        var items = new[] { "CalcDi", "CalcDisk", "CalcDisc" };

        var result = NameCandidates.Rank("CalcDisc", items, n => n);

        Assert.That(result.Select(r => r.Item).ToList(), Is.EqualTo(new[] { "CalcDisc", "CalcDisk", "CalcDi" }));
        Assert.That(result.Select(r => r.Distance).ToList(), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Rank_EqualDistances_KeepInputOrder()
    {
        var items = new[] { "CalcDisx", "CalcDisk" };

        var result = NameCandidates.Rank("CalcDisc", items, n => n);

        Assert.That(result.Select(r => r.Item).ToList(), Is.EqualTo(new[] { "CalcDisx", "CalcDisk" }));
        Assert.That(result.Select(r => r.Distance).ToList(), Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void Rank_FiveSimilarItems_CapsAtThreeByDefault()
    {
        var items = new[] { "CalcDisk", "CalcDisx", "CalcDisp", "CalcDisa", "CalcDisc" };

        var result = NameCandidates.Rank("CalcDisc", items, n => n);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Item, Is.EqualTo("CalcDisc"));
        Assert.That(result.Select(r => r.Item).ToList(), Is.EqualTo(new[] { "CalcDisc", "CalcDisk", "CalcDisx" }));
    }

    [Test]
    public void Rank_CapParameterPassed_LimitsResultCount()
    {
        var items = new[] { "CalcDisk", "CalcDisx", "CalcDisp", "CalcDisa", "CalcDisc" };

        var result = NameCandidates.Rank("CalcDisc", items, n => n, cap: 1);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Item, Is.EqualTo("CalcDisc"));
    }

    [Test]
    public void Rank_LowerCaseWanted_FindsPascalCaseNameWithDistanceZero()
    {
        var result = NameCandidates.Rank("calcdisc", new[] { "CalcDisc" }, n => n);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Item, Is.EqualTo("CalcDisc"));
        Assert.That(result[0].Distance, Is.EqualTo(0));
    }

    [Test]
    public void Compare_CaseOnlyDifference_IsSimilarWithDistanceZero()
    {
        var (similar, distance) = NameCandidates.Compare("calcdisc", "CalcDisc");

        Assert.That(similar, Is.True);
        Assert.That(distance, Is.EqualTo(0));
    }

    [Test]
    public void Rank_EmptyItems_ReturnsEmpty()
    {
        var result = NameCandidates.Rank("CalcDisc", Array.Empty<string>(), n => n);

        Assert.That(result, Is.Empty);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("a")]
    [TestCase("ab")]
    public void Rank_BlankOrShortWanted_ReturnsEmpty(string wanted)
    {
        var result = NameCandidates.Rank(wanted, new[] { "ab", "abc", "CalcDisc" }, n => n);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Compare_CalcDiscAgainstLongerFullName_IsSimilar()
    {
        var (similar, _) = NameCandidates.Compare("CalcDisc", "CalculateDiscountedTotal");

        Assert.That(similar, Is.True);
    }

    // ---- strict mode: edit distance only (solution-wide symbol candidates) ----

    [Test]
    public void Rank_Strict_TypoStillRanksCalcDiscFirst()
    {
        var names = new[] { "ApplyDiscount", "CalcDisc", "OrderCheckout" };

        var result = NameCandidates.Rank("CalcDsc", names, n => n, strict: true);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Item, Is.EqualTo("CalcDisc"));
        Assert.That(result[0].Distance, Is.EqualTo(1));
    }

    [Test]
    public void Rank_Strict_SharedPrefixOnlyNames_ReturnsNothingWhileDefaultKeepsTheGenerousRule()
    {
        var names = new[] { "ApplyDiscount", "ApplyPercentage", "CalcDisc", "OrderCheckout" };

        var strict = NameCandidates.Rank("ApplyLoyaltyBonus", names, n => n, strict: true);
        var generous = NameCandidates.Rank("ApplyLoyaltyBonus", names, n => n);

        Assert.That(strict, Is.Empty, "a shared 4-character prefix alone is not similarity in strict mode");
        Assert.That(generous.Select(r => r.Item), Is.EquivalentTo(new[] { "ApplyDiscount", "ApplyPercentage" }), "the default rule is unchanged");
    }

    [Test]
    public void Compare_Strict_DropsContainmentAndSubsequenceCriteria()
    {
        var (strictSimilar, strictDistance) = NameCandidates.Compare("CalcDisc", "CalculateDiscountedTotal", strict: true);
        var (defaultSimilar, defaultDistance) = NameCandidates.Compare("CalcDisc", "CalculateDiscountedTotal");

        Assert.That(strictSimilar, Is.False);
        Assert.That(defaultSimilar, Is.True);
        Assert.That(strictDistance, Is.EqualTo(defaultDistance));
    }
}
