namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// A small static calculator used as the source fixture for NameCandidates tests. Its three public static methods are the
/// "member names" a not-found hint is ranked against. Kept as a raw string so tests can hand it to TestSolutionBuilder.
/// </summary>
public static class OrderPricingFixture
{
    public const string FileName = "OrderPricingCalculator.cs";

    public const string Source = """
        namespace Fixture;

        public static class OrderPricingCalculator
        {
            public static decimal CalcDisc(decimal subtotal, decimal rate) => subtotal - subtotal * rate;

            public static decimal OrderCheckout(decimal subtotal) => subtotal;

            public static decimal GetTotal(decimal subtotal) => subtotal;
        }
        """;
}
