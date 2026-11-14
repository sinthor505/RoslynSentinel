using System.Text.RegularExpressions;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Targeted scenario: a single rename step (CalcDisc to CalculateDiscountedTotal) where the model must
/// also update the caller in OrderCheckout.cs. Plan: plan_targeted_tool_scenario_evals.
/// </summary>
public static class RenameScenarios
{
    public const string CalcDiscRenameId = "CalcDiscRename";

    private static readonly Regex CalculateDiscountedTotalName = new(@"\bCalculateDiscountedTotal\b", RegexOptions.CultureInvariant);
    private static readonly Regex CalcDiscName = new(@"\bCalcDisc\b", RegexOptions.CultureInvariant);

    public static readonly ScenarioDefinition CalcDiscRename = new(
        Id: CalcDiscRenameId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: CalcDiscRenamePrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "RenameSymbol",
        CheckFinalState: CheckFinalStateAsync,
        SourceRun: "10-chain batches F and G, rename step (CalcDisc to CalculateDiscountedTotal written inside a ReplaceSnippet edit); see docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md; run and turn not recorded here",
        ReproducesAt: "measured by this scenario with ROSLYNSENTINEL_LIFT_RENAMES off and on; see the results recorded in plan_targeted_tool_scenario_evals step 5b",
        FixedBy: "ROSLYNSENTINEL_LIFT_RENAMES flag (default off)");

    internal const string CalcDiscRenamePrompt = """
        # Task: One small refactor in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDisc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply the step below)

        1. **Rename**: Rename `CalcDisc` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` - that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.
        """;

    /// <summary>
    /// Reads both fixture files after the run and returns the verdict. A throw from the functional
    /// check means the edited code did not build or run, so it is scored Wrong; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var calculatorText = await ReadOrNullAsync(ScenarioFixtureFiles.Absolute(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath), cancellationToken);
        var checkoutText = await ReadOrNullAsync(ScenarioFixtureFiles.Absolute(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath), cancellationToken);

        var verdict = ClassifyFiles(calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        try
        {
            var (preferred, standard) = await FunctionalFixVerifier.InvokeCalculateDiscountedTotalAsync(
                ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), 100m, 0.1m, cancellationToken);
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
            // A build or invoke failure means the rename left code that does not compile or run: Wrong.
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

        if (CalculateDiscountedTotalName.IsMatch(calculatorText)
            && CalculateDiscountedTotalName.IsMatch(checkoutText)
            && !CalcDiscName.IsMatch(calculatorText)
            && !CalcDiscName.IsMatch(checkoutText))
        {
            return FinalStateVerdict.Correct;
        }

        return FinalStateVerdict.Wrong;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static async Task<string?> ReadOrNullAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
}
