using Microsoft.CodeAnalysis;

using RoslynSentinel.Tests.Battery.Basic;

using CommonResolver = RoslynSentinel.Common.MissingParameterListResolver;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The shared parameter-list-less method id fallback (<see cref="CommonResolver"/>, decision 3 of
/// plan_not_found_error_orientation). The OrderPricing fixture declares CalcDisc, OrderCheckout and GetTotal on the static
/// class OrderPricingCalculator and no ApplyLoyaltyBonus; the Gadget fixture adds an overloaded Pick and a generic Echo.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")]
public class MissingParameterListResolverTests
{
    private const string ProjectName = "TestProj";

    private const string GadgetSource = """
        namespace Fixture;

        public class Gadget
        {
            public static int Pick(int value) => value;
            public static string Pick(string value) => value;
            public static T Echo<T>(T value) => value;
        }
        """;

    private static Solution BuildSolution()
    {
        var files = new List<(string name, string content)>
        {
            (OrderPricingFixture.FileName, OrderPricingFixture.Source),
            ("Gadget.cs", GadgetSource),
        };
        return TestSolutionBuilder.CreateSolutionWithProject(ProjectName, files.ToArray());
    }

    [Test]
    public async Task FindAsync_IdWithoutParameterList_UniqueMethod_ReturnsItWithNoOverloadIds()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "M:Fixture.OrderPricingCalculator.CalcDisc");

        Assert.That(unique, Is.Not.Null);
        Assert.That(unique!.Name, Is.EqualTo("CalcDisc"));
        Assert.That(unique.ContainingType.Name, Is.EqualTo("OrderPricingCalculator"));
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_TypeWithTwoOverloads_ReturnsNoUniqueAndBothFullIds()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "M:Fixture.Gadget.Pick");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.EquivalentTo(new[]
        {
            "M:Fixture.Gadget.Pick(System.Int32)",
            "M:Fixture.Gadget.Pick(System.String)",
        }));
    }

    [Test]
    public async Task FindAsync_IdAlreadyHasParentheses_ReturnsNothing()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(
            BuildSolution(), ProjectName, "M:Fixture.OrderPricingCalculator.CalcDisc(System.Decimal,System.Decimal)");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_IdNotAMethodId_ReturnsNothing()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "T:Fixture.OrderPricingCalculator.CalcDisc");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_UnknownType_ReturnsNothing()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "M:Fixture.NoSuchType.CalcDisc");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_UnknownProject_ReturnsNothing()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), "NoSuchProject", "M:Fixture.OrderPricingCalculator.CalcDisc");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_NoSuchMethodOnRealType_ReturnsNothing()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "M:Fixture.OrderPricingCalculator.ApplyLoyaltyBonus");

        Assert.That(unique, Is.Null);
        Assert.That(overloadIds, Is.Empty);
    }

    [Test]
    public async Task FindAsync_GenericMethodIdWithAritySuffix_MatchesByBareName()
    {
        var (unique, overloadIds) = await CommonResolver.FindAsync(BuildSolution(), ProjectName, "M:Fixture.Gadget.Echo``1");

        Assert.That(unique, Is.Not.Null);
        Assert.That(unique!.Name, Is.EqualTo("Echo"));
        Assert.That(overloadIds, Is.Empty);
    }
}
