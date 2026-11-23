using System.Text.RegularExpressions;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Targeted scenario: two rename steps in one run (CalcDisc to CalculateDiscountedTotal, then the class
/// OrderPricingCalculator to StandardOrderPricingCalculator), with callers in OrderCheckout.cs that must
/// follow both. Plan: plan_targeted_tool_scenario_evals.
/// </summary>
public static class TwoRenameScenarios
{
    public const string TwoRenamesWholeFileId = "TwoRenamesWholeFile";

    private static readonly Regex CalculateDiscountedTotalName = new(@"\bCalculateDiscountedTotal\b", RegexOptions.CultureInvariant);
    private static readonly Regex StandardOrderPricingCalculatorName = new(@"\bStandardOrderPricingCalculator\b", RegexOptions.CultureInvariant);
    private static readonly Regex StandardClassDeclaration = new(@"\bclass\s+StandardOrderPricingCalculator\b", RegexOptions.CultureInvariant);
    private static readonly Regex OldClassDeclaration = new(@"\bclass\s+OrderPricingCalculator\b", RegexOptions.CultureInvariant);
    private static readonly Regex OldClassName = new(@"\bOrderPricingCalculator\b", RegexOptions.CultureInvariant);
    private static readonly Regex CalcDiscName = new(@"\bCalcDisc\b", RegexOptions.CultureInvariant);

    public static readonly ScenarioDefinition TwoRenamesWholeFile = new(
        Id: TwoRenamesWholeFileId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: TwoRenamesWholeFilePrompt,
        TurnCap: 10,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "RenameSymbol",
        CheckFinalState: CheckFinalStateAsync,
        SourceRun: "10-chain batch H2, run 20261111-093725-368, turn 3 (one whole-file ReplaceSnippet renaming CalcDisc and the class, then 11 identical redirected resends); see docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md",
        ReproducesAt: "measured by this scenario with ROSLYNSENTINEL_LIFT_RENAMES off and on; see plan_targeted_tool_scenario_evals step 6",
        FixedBy: "multi-rename lift 2ef7d88e (needs ROSLYNSENTINEL_LIFT_RENAMES) and redirect naming every rename 1e315a2c");

    private const string TwoRenamesWholeFilePrompt = """
        # Task: Two renames in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a class `OrderPricingCalculator` with a
        method `CalcDisc` that computes an order's discounted total. Both are used from one other
        file, `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply both)

        1. **Rename the method**: Rename `CalcDisc` to `CalculateDiscountedTotal`. The call in
           `OrderCheckout.cs` must be updated to the new name too.
        2. **Rename the class**: Rename the class `OrderPricingCalculator` to
           `StandardOrderPricingCalculator`. `OrderCheckout.cs` declares a field of that type and
           constructs it with `new OrderPricingCalculator()`, so that file must be updated too. A
           rename that only changes a declaration and misses its users is not complete.
        """;

    /// <summary>
    /// Reads both fixture files after the run and returns the verdict. A throw from the functional
    /// check means the edited code did not build or run, so it is scored Wrong; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadCalculatorOrNullAsync(solutionDirectory, cancellationToken);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);

        var verdict = ClassifyFiles(calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        try
        {
            var (preferred, standard) = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalAsync(
                ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), 100m, 0.1m, cancellationToken,
                calculatorTypeName: "StandardOrderPricingCalculator");
            return Math.Abs(preferred - 89.0m) <= 0.001m && Math.Abs(standard - 90m) <= 0.001m
                ? FinalStateVerdict.Correct
                : FinalStateVerdict.Wrong;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A build or invoke failure means the renames left code that does not compile or run: Wrong.
            return FinalStateVerdict.Wrong;
        }
    }

    /// <summary>Pure classification of the two fixture texts (null when the file is missing).</summary>
    public static FinalStateVerdict ClassifyFiles(string? calculatorText, string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(calculatorText)
            || string.IsNullOrWhiteSpace(checkoutText)
            || !calculatorText.Contains("SummarizeShipping", StringComparison.Ordinal)
            || !calculatorText.Contains("DescribeOrder", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        if (Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent)
            && Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent))
        {
            return FinalStateVerdict.Unchanged;
        }

        if (StandardClassDeclaration.IsMatch(calculatorText)
            && CalculateDiscountedTotalName.IsMatch(calculatorText)
            && !CalcDiscName.IsMatch(calculatorText)
            && !OldClassDeclaration.IsMatch(calculatorText)
            && CalculateDiscountedTotalName.IsMatch(checkoutText)
            && StandardOrderPricingCalculatorName.IsMatch(checkoutText)
            && !CalcDiscName.IsMatch(checkoutText)
            && !OldClassName.IsMatch(checkoutText))
        {
            return FinalStateVerdict.Correct;
        }

        return FinalStateVerdict.Wrong;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
