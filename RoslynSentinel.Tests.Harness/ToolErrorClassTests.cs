using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

// Result envelopes copied from archived transcripts (C:\tmp\rs_batchA\samples_3a.json). Each literal is that
// sample's resultJson string body verbatim, so its escapes decode to the real envelope. Only toolCall.arguments
// was replaced with {} (not used by the classifier), and for the NotFound sample errorData.message was trimmed
// to its first sentence (it ran over 1500 chars); errorData.structuredDetail is kept verbatim.
[TestFixture]
public class ToolErrorClassTests
{
    private const string RedirectResultJson = "{\"toolCall\":{\"toolCallId\":\"23415d9d21fa\",\"name\":\"ReplaceSnippet\",\"arguments\":{}},\"solutionRoot\":\"C:\\\\Users\\\\Administrator\\\\AppData\\\\Local\\\\Temp\\\\2\\\\RoslynSentinelTests_07c45580-7e8f-4612-8089-398919779d81\",\"isError\":true,\"errorData\":{\"errorCode\":\"ValidationFailed\",\"message\":\"ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Next step: This edit renames CalcDisc to CalculateDiscountedTotal and 1 file(s) still use CalcDisc: use RenameSymbol(reason: \\\"<why you call this>\\\", projectName: \\\"ContosoOrders.Core\\\", docCommentId: \\\"M:ContosoOrders.Core.FixtureHelpers.OrderPricingCalculator.CalcDisc(System.Decimal,System.Decimal,System.Boolean)\\\", newName: \\\"CalculateDiscountedTotal\\\") instead.\\r\\nEdit impact (symbols this edit changed that unedited files still use):\\r\\n  - renamed CalcDisc -> CalculateDiscountedTotal (OrderPricingCalculator.cs:22)\\r\\n      OrderCheckout.cs:9  return _calculator.CalcDisc(amount, rate, isPreferredCustomer); Compiler errors caused by this edit:\\n[COMPILER ERROR]\\nCS1061 at ContosoOrders.Core\\\\FixtureHelpers\\\\OrderCheckout.cs:9: 'OrderPricingCalculator' does not contain a definition for 'CalcDisc' and no accessible extension method 'CalcDisc' accepting a first argument of type 'OrderPricingCalculator' could be found (are you missing a using directive or an assembly reference?)\\n    > return _calculator.CalcDisc(amount, rate, isPreferredCustomer);\\n  'CalcDisc' is declared in OrderPricingCalculator.cs, which this edit changes. If the edit removes or renames 'CalcDisc', this file still uses the old name: change the declaration and every use in one call.\"},\"findings\":[],\"hasMoreData\":false}";

    private const string CompileGateResultJson = "{\"toolCall\":{\"toolCallId\":\"8f18a0088b1e\",\"name\":\"ReplaceSnippet\",\"arguments\":{}},\"solutionRoot\":\"C:\\\\Users\\\\Administrator\\\\AppData\\\\Local\\\\Temp\\\\2\\\\RoslynSentinelTests_07c45580-7e8f-4612-8089-398919779d81\",\"isError\":true,\"errorData\":{\"errorCode\":\"ValidationFailed\",\"message\":\"ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\\n[COMPILER ERROR]\\nCS0103 (x2): The name 'CalculateProduct' does not exist in the current context at: ContosoOrders.Core\\\\FixtureHelpers\\\\OrderPricingCalculator.cs:29, ContosoOrders.Core\\\\FixtureHelpers\\\\OrderPricingCalculator.cs:33\\n    > var discount = CalculateProduct(amount, discountRate) * 1.1m;\\n  No symbol named 'CalculateProduct' was found anywhere in the solution. This is likely a typo, or the member genuinely doesn't exist yet and needs to be added.\"},\"findings\":[],\"hasMoreData\":false}";

    // NotFound: errorData.message trimmed to its first sentence (see the header comment); structuredDetail verbatim.
    private const string NotFoundResultJson = "{\"toolCall\":{\"toolCallId\":\"5852ebb42a99\",\"name\":\"ReplaceSnippet\",\"arguments\":{}},\"solutionRoot\":\"C:\\\\Users\\\\Administrator\\\\AppData\\\\Local\\\\Temp\\\\2\\\\RoslynSentinelTests_33e3f6c1-5beb-4c78-a79f-06a12fa8568e\",\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"ReplaceSnippet apply for 'ContosoOrders.Core\\\\FixtureHelpers\\\\OrderPricingCalculator.cs' failed: oldContent was not found: it matches nothing at any whitespace tolerance probed.\",\"structuredDetail\":[{\"outcome\":\"NoMatch\",\"requestedTolerance\":\"IgnoreLineEndings\",\"requestedCaseMode\":\"CaseSensitive\",\"tiersProbed\":[\"Exact\",\"IgnoreLineEndings\",\"IgnoreIndentation\",\"IgnoreAllWhitespace\"],\"closestWindows\":[{\"startLine\":1,\"actualLines\":[\"namespace ContosoOrders.Core.FixtureHelpers;\",\"\",\"public class OrderPricingCalculator\",\"{\",\"    private readonly object _unrelatedField = new();\",\"\",\"    public string DescribeOrder( int    id , string label )\",\"    {\",\"            return $\\\"Order {id}: {label}\\\";\",\"    }\",\"\",\"    /// <summary>\",\"    /// Computes the final total for an order after discount. BUG-FREE but duplicated: both\",\"    /// branches independently multiply the raw order amount by the discount rate instead\",\"    /// of sharing one expression -> the model is asked to extract that shared calculation\",\"    /// into its own method. Deliberately worded without the literal expression text so a\",\"    /// model that leaves this comment untouched (it isn't asked to update comments) can't\",\"    /// be mistaken for one that left the duplicated code in place.\",\"    /// </summary>\",\"    public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)\",\"    {\",\"        if (isPreferredCustomer)\",\"        {\",\"            var discount = amount * rate * 1.1m;\",\"            return amount - discount;\",\"        }\",\"\",\"        var standardDiscount = amount * rate;\",\"        return amount - standardDiscount;\",\"    }\",\"\",\"    public string SummarizeShipping(  int   zone  )\",\"    {\",\"            return zone switch\",\"            {\",\"                1 => \\\"local\\\",\",\"                2 => \\\"regional\\\",\",\"                _ => \\\"national\\\",\",\"            };\",\"    }\",\"}\"],\"score\":0.9899759422614275}],\"closestWindowTotalCount\":1,\"nearMatchTotalCount\":0,\"filterTrail\":[],\"candidatesBeforeSelectors\":0,\"candidatesAfterSelectors\":0}]},\"findings\":[],\"hasMoreData\":false}";

    private const string NoCodeResultJson = "{\"toolCall\":{\"toolCallId\":\"fc8df63354c2\",\"name\":\"Git\",\"arguments\":{}},\"message\":\"'rewrite' is not a valid value for parameter 'operation' of tool 'Git'. Valid values are: status, log, diff, show, stage, add, unstage, commit, revert, reset, abort, hunks. Nothing was executed.\"}";

    [Test]
    public void ClassifyError_RedirectSample_IsGuardrailRedirect()
    {
        var call = Call(RedirectResultJson);

        Assert.That(AgentToolErrorAssertions.ClassifyError(call), Is.EqualTo(ToolErrorClass.GuardrailRedirect));
        Assert.That(AgentToolErrorAssertions.ErrorCodeOf(call), Is.EqualTo("ValidationFailed"));
    }

    [Test]
    public void ClassifyError_CompileGateSample_IsCompileGateRejection()
    {
        Assert.That(AgentToolErrorAssertions.ClassifyError(Call(CompileGateResultJson)), Is.EqualTo(ToolErrorClass.CompileGateRejection));
    }

    [Test]
    public void ClassifyError_NotFoundSample_IsNotFound()
    {
        var call = Call(NotFoundResultJson);

        Assert.That(AgentToolErrorAssertions.ClassifyError(call), Is.EqualTo(ToolErrorClass.NotFound));
        Assert.That(AgentToolErrorAssertions.ErrorCodeOf(call), Is.EqualTo("NotFound"));
    }

    [Test]
    public void ClassifyError_SampleWithoutCode_IsNoCode()
    {
        var call = Call(NoCodeResultJson);

        Assert.That(AgentToolErrorAssertions.ClassifyError(call), Is.EqualTo(ToolErrorClass.NoCode));
        Assert.That(AgentToolErrorAssertions.ErrorCodeOf(call), Is.Null);
    }

    [Test]
    public void ClassifyError_SuccessfulCall_IsNone()
    {
        Assert.That(AgentToolErrorAssertions.ClassifyError(Call(RedirectResultJson, isError: false)), Is.EqualTo(ToolErrorClass.None));
    }

    [Test]
    public void ErrorCodeOf_IgnoresErrorCodeTypedInsideArguments()
    {
        const string resultJson = "{\"toolCall\":{\"arguments\":{\"errorCode\":\"Fake\"}},\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}";

        Assert.That(AgentToolErrorAssertions.ErrorCodeOf(Call(resultJson)), Is.EqualTo("NotFound"));
    }

    private static AgentToolCallRecord Call(string resultJson, bool isError = true) => new()
    {
        ToolName = "ReplaceSnippet",
        ArgumentsJson = "{}",
        ResultJson = resultJson,
        IsError = isError,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };
}
