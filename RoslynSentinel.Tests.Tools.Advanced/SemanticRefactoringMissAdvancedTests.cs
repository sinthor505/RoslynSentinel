using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the SemanticRefactoringEngine lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 2).
// WrapInUsingAsync (line-range form) is driven through WrapRange(wrapper: using), the tool that hosts it: a range holding no statement states the
// file's line count as the fact and offers GetFileOutline, and nothing is written (the file text is compared before and after).
// ConvertPropertyToMethodsAsync(filePath, className, propertyName) is not called by any tool (the ConvertPropertyToMethods transform runs through
// CodeStyleEngine, a different method), so its class and property misses are tested engine-direct: ranked candidates, and the no-similar-name fact.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SemanticRefactoringMissAdvancedTests
{
    private const string InvoiceFile = "Invoice.cs";

    private const string InvoiceSource = """
        namespace Billing;

        public class Invoice
        {
            public int Number { get; set; }
            public string Customer { get; set; }

            public int Total()
            {
                var sum = 1;
                return sum;
            }
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("Choose a range"), "No instruction sentence.");
    }

    // ---- WrapRange(using, startLine, endLine): WrapInUsingAsync ----

    [Test]
    [Category("SemanticRefactoringEngine")]
    public async Task WrapRangeUsing_RangeWithNoStatements_StatesTheLineCountAndOffersTheOutlineAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((InvoiceFile, InvoiceSource));
        var before = workspace.ReadText(InvoiceFile);
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);

        // Lines 5-6 are two property declarations: no statement of a block body lies inside them.
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf(InvoiceFile), 5, 6, WrapRangeWrapper.@using, "resource");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData.Message);
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No statements were found in lines 5-6 of"));
        Assert.That(message, Does.Match(@"which has \d+ line\(s\)"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(InvoiceFile), Is.EqualTo(before), "A refused range must not write the file.");
    }

    // ---- ConvertPropertyToMethodsAsync: no tool hosts it, so engine-direct ----

    private const string PricingSource = """
        namespace Billing;

        public class OrderPricingCalculator
        {
            public decimal UnitPrice { get; set; }
        }
        """;

    private static async Task<ToolNotFoundException> ConvertMissAsync(string className, string propertyName)
    {
        using var workspace = InMemoryWorkspace.Create(("Pricing.cs", PricingSource));
        var before = workspace.ReadText("Pricing.cs");
        var engine = Battery.Basic.TestEngines.SemanticRefactoringEngine(workspace.Manager);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.ConvertPropertyToMethodsAsync(workspace.PathOf("Pricing.cs"), className, propertyName));

        Assert.That(workspace.ReadText("Pricing.cs"), Is.EqualTo(before), "A refused lookup must not write the file.");
        return ex!;
    }

    [Test]
    [Category("SemanticRefactoringEngine")]
    public async Task ConvertPropertyToMethods_MisspeltClass_OffersTheNearClassAsync()
    {
        var ex = await ConvertMissAsync("OrderPricingCalculatr", "UnitPrice");
        Assert.That(ex.Message, Does.Contain("Did you mean 'OrderPricingCalculator'? Retry with className: 'OrderPricingCalculator'."));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    [Category("SemanticRefactoringEngine")]
    public async Task ConvertPropertyToMethods_NoSimilarClass_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var ex = await ConvertMissAsync("Zzzqqq", "UnitPrice");
        Assert.That(ex.Message, Does.Contain("has a similar name to 'Zzzqqq'."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        Assert.That(ex.Message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    [Category("SemanticRefactoringEngine")]
    public async Task ConvertPropertyToMethods_MisspeltProperty_OffersTheNearPropertyAndStatesTheSearchedClassAsync()
    {
        var ex = await ConvertMissAsync("OrderPricingCalculator", "UnitPrce");
        Assert.That(ex.Message, Does.Contain("Did you mean 'UnitPrice'? Retry with propertyName: 'UnitPrice'."));
        Assert.That(ex.Message, Does.Contain("Only the properties declared directly in class 'OrderPricingCalculator' were searched."));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    [Category("SemanticRefactoringEngine")]
    public async Task ConvertPropertyToMethods_NoSimilarProperty_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var ex = await ConvertMissAsync("OrderPricingCalculator", "Zzzqqq");
        Assert.That(ex.Message, Does.Contain("has a similar name to 'Zzzqqq'."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        Assert.That(ex.Message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(ex.Message);
    }
}
