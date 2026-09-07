namespace RoslynSentinel.Tests.ModelEval.Fixtures;

/// <summary>
/// Multi-step refactoring fixture — unlike <see cref="WholeFileRewriteReproducer"/> and
/// <see cref="SizeGraduatedReproducer"/> (both single-bug-fix scenarios at varying prompt-guidance
/// levels), this exercises a chain of three independent, ordinary refactoring operations against
/// one small class: extract a duplicated expression into a new method, rename an existing method
/// (with a real cross-file call site that must be updated), and change the accessibility of the
/// newly extracted method. Each step is mechanically verifiable on its own, so a partial-credit
/// failure (e.g. rename done but accessibility step skipped) is distinguishable from total failure.
/// Uses plain arithmetic rather than real Roslyn APIs so it compiles inside
/// <see cref="RoslynSentinel.Tests.TestSolutionFixture"/>'s copy of Samples/ContosoOrders, which has
/// no NuGet packages and no restore step.
/// </summary>
public static class OrderPricingRefactorReproducer
{
    /// <summary>
    /// Goes in the fixture at ContosoOrders.Core/FixtureHelpers/OrderPricingCalculator.cs. Padded
    /// with unrelated members before and after the target method, used to detect an accidental
    /// whole-file/whole-class logic touch (reformatting them is fine — only a change to their
    /// signature or behavior counts as a violation; see <c>OrderPricingRefactorAgentTests</c>'s
    /// semantic, whitespace-insensitive comparison). <c>CalcDisc</c> computes the same
    /// "amount * rate" discount expression twice (once per branch) — the duplication the model is
    /// asked to extract into a new method — and is itself the method the model is asked to rename.
    /// </summary>
    public const string StartingCalculatorFileContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public class OrderPricingCalculator
        {
            private readonly object _unrelatedField = new();

            public string DescribeOrder( int    id , string label )
            {
                    return $"Order {id}: {label}";
            }

            /// <summary>
            /// Computes the final total for an order after discount. BUG-FREE but duplicated: both
            /// branches independently multiply the raw order amount by the discount rate instead
            /// of sharing one expression — the model is asked to extract that shared calculation
            /// into its own method. Deliberately worded without the literal expression text so a
            /// model that leaves this comment untouched (it isn't asked to update comments) can't
            /// be mistaken for one that left the duplicated code in place.
            /// </summary>
            public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)
            {
                if (isPreferredCustomer)
                {
                    var discount = amount * rate * 1.1m;
                    return amount - discount;
                }

                var standardDiscount = amount * rate;
                return amount - standardDiscount;
            }

            public string SummarizeShipping(  int   zone  )
            {
                    return zone switch
                    {
                        1 => "local",
                        2 => "regional",
                        _ => "national",
                    };
            }
        }
        """;

    /// <summary>
    /// Goes in the fixture at ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs — a second,
    /// unrelated-looking file with one real call site into <c>CalcDisc</c>, standing in for the
    /// "rename missed a call site in another file" failure mode a same-file-only rename could hide.
    /// A correct rename must update this call along with any others; a rename tool used correctly
    /// (e.g. RenameSymbol) updates this automatically, while a model that hand-edits only
    /// OrderPricingCalculator.cs will leave this call referencing the old, now-missing name.
    /// </summary>
    public const string CheckoutCallerFileContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public class OrderCheckout
        {
            private readonly OrderPricingCalculator _calculator = new();

            public decimal GetFinalPrice(decimal amount, decimal rate, bool isPreferredCustomer)
            {
                return _calculator.CalcDisc(amount, rate, isPreferredCustomer);
            }
        }
        """;

    /// <summary>
    /// Goes in the fixture at ContosoOrders.Tests/ModelEvalGenerated/OrderCheckoutTests.cs — a real
    /// xunit test against <c>OrderCheckout.GetFinalPrice</c>, the "front door" per
    /// docs/current/modeleval_fixture_test_suite_redesign.md: <c>GetFinalPrice</c>'s own name/shape
    /// is never touched by this fixture's refactor steps, only its callee
    /// (<c>CalcDisc</c> -> <c>CalculateDiscountedTotal</c>) is, so a test written against it pre-fix
    /// still compiles and is meaningful after a correct rename/extraction — unlike a test that named
    /// <c>CalcDisc</c> directly, which would fail to COMPILE post-rename. Replaces the old
    /// CollapseWhitespace substring checks: proving GetFinalPrice's behavior survived proves the
    /// renamed/extracted logic underneath it still works, without caring what it's now called or how
    /// many methods it's split across.
    /// </summary>
    public const string CheckoutFrontDoorTestsFileContent = """
        using ContosoOrders.Core.FixtureHelpers;

        using Xunit;

        namespace ContosoOrders.Tests;

        public class OrderCheckoutTests
        {
            [Fact]
            public void GetFinalPrice_PreferredCustomer_AppliesScaledDiscount()
            {
                var checkout = new OrderCheckout();

                var result = checkout.GetFinalPrice(100m, 0.1m, isPreferredCustomer: true);

                Assert.Equal(89.0m, result);
            }

            [Fact]
            public void GetFinalPrice_StandardCustomer_AppliesUnscaledDiscount()
            {
                var checkout = new OrderCheckout();

                var result = checkout.GetFinalPrice(100m, 0.1m, isPreferredCustomer: false);

                Assert.Equal(90m, result);
            }
        }
        """;
}
