using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Lookup misses of MsToolAugmentEngine.GenerateToStringSafeAsync reached through CodeTransformTools.Generate(generate_to_string_safe)
/// (plan_not_found_error_orientation Step 5e slice 4): the type miss (a near name gives the ranked retry, no near name gives the
/// no-similar-name fact and a GetFileOutline option) and the file miss of the shared ReadSourceAsync (a near file gives the candidate list,
/// no near file gives the no-similar-name fact). The type miss comes back as the failed MsAugmentResult the tool returns as its payload
/// (see the TODO at the call in CodeTransformTools); the file miss is a thrown ToolNotFoundException the tool maps to a NotFound error.
/// Neither writes: the tool never writes for this kind, and no transform result carries new text.
/// </summary>
[TestFixture]
[Category("CodeTransformTools")]
public class MsToolAugmentGenerateToStringMissTests : CodeTransformMissFixtureBase
{
    private const string PricingSource = """
        public class OrderPricingCalculator
        {
            public int Rate { get; set; }

            public int ComputeTotal(int tax) => Rate + tax;
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // The failed MsAugmentResult the tool returns as its payload: NotFound code, no new text.
    private static string AssertFailedResult(SentinelCallToolResult<object> result)
    {
        Assert.That(result.SuccessData, Is.TypeOf<MsAugmentResult>());
        var failed = (MsAugmentResult)result.SuccessData!;
        Assert.That(failed.IsError, Is.True);
        Assert.That(failed.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(failed.UpdatedContent, Is.Null, "nothing was generated or written");
        return failed.Error!;
    }

    [Test]
    public async Task GenerateToString_NearTypeName_OffersTheRankedRetry()
    {
        var path = SetSource("Pricing.cs", PricingSource);

        var message = AssertFailedResult(await Tools.Generate(CodeTransformKind.generate_to_string_safe, "miss test", path, className: "OrderPricingCalculatr"));

        Assert.That(message, Does.Contain("Type 'OrderPricingCalculatr' was not found in"));
        Assert.That(message, Does.Contain("Did you mean 'OrderPricingCalculator'? Retry with typeName: 'OrderPricingCalculator'."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task GenerateToString_NoSimilarTypeName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Pricing.cs", PricingSource);

        var message = AssertFailedResult(await Tools.Generate(CodeTransformKind.generate_to_string_safe, "miss test", path, className: "Qzxvbnm"));

        Assert.That(message, Does.Contain("No type declared in"));
        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task GenerateToString_MisspeltFile_OffersTheNearFile()
    {
        var path = SetSource("Pricing.cs", PricingSource);
        var missing = Path.Combine(Path.GetDirectoryName(path)!, "Pricng.cs");

        var message = AssertNotFound(await Tools.Generate(CodeTransformKind.generate_to_string_safe, "miss test", missing, className: "OrderPricingCalculator"));

        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Pricing.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task GenerateToString_NoSimilarFile_StatesTheNoCandidateFact()
    {
        var path = SetSource("Pricing.cs", PricingSource);
        var missing = Path.Combine(Path.GetDirectoryName(path)!, "Zzzqqq.cs");

        var message = AssertNotFound(await Tools.Generate(CodeTransformKind.generate_to_string_safe, "miss test", missing, className: "OrderPricingCalculator"));

        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }
}
