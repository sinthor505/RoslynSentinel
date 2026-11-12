using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Targeted scenario: one run mixing single-edit and batched ReplaceSnippet calls - two literal changes in
/// SummarizeShipping and one doc comment above GetFinalPrice, across two files. Regression guard for the
/// ReplaceSnippet shape confusion (filePath beside batchEdits) seen in the 10-chain frozen batch A and batch H5 runs.
/// </summary>
public static class BatchShapeScenarios
{
    public const string MixedSingleAndBatchShapeId = "MixedSingleAndBatchShape";

    private const string MixedSingleAndBatchShapePrompt = """
        # Task: Three small text edits in two files

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `SummarizeShipping` that returns a label per shipping zone. `{0}/FixtureHelpers/OrderCheckout.cs` has a method `GetFinalPrice`.

        ## Steps (apply all three)

        1. In `SummarizeShipping`, change the string "local" to "local-pickup".
        2. In `SummarizeShipping`, change the string "regional" to "regional-courier".
        3. In `OrderCheckout.cs`, add the doc comment `/// <summary>Returns the price after discount.</summary>` on its own line directly above `GetFinalPrice`.

        Make all three edits with the `ReplaceSnippet` tool, in as few calls as you can. Change nothing else.
        """;

    public static readonly ScenarioDefinition MixedSingleAndBatchShape = new(
        Id: MixedSingleAndBatchShapeId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: MixedSingleAndBatchShapePrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReplaceSnippet",
        CheckFinalState: CheckFinalStateAsync,
        SourceRun: "10-chain frozen batch A run 101 turn 17 (4 occurrences in the batch) and batch H5 run 20261111-223133-011 turn 2 (ReplaceSnippet: supply either filePath/oldContent/newContent or 'batchEdits', not both); see docs/current/evals/eval_tenchain_frozen_batch_2026_11_10.md and eval_tenchain_reasoning_replay_2026_11_10.md",
        ReproducesAt: "measured by this scenario (a top-level filePath beside batchEdits is accepted since e0ceceab; oldContent or newContent beside batchEdits is still rejected, so the mode is open)",
        FixedBy: "e0ceceab for filePath beside batchEdits only");

    /// <summary>
    /// Reads the calculator and checkout after a run and returns the verdict. The build check runs only
    /// when the files classify as Correct; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, cancellationToken);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);

        var verdict = ClassifyFiles(calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        var built = await FunctionalFixVerifier.BuildSucceedsAsync(ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), cancellationToken);
        return built ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    /// <summary>Pure classification of the two fixture texts (null when the file is missing).</summary>
    public static FinalStateVerdict ClassifyFiles(string? calculatorText, string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(calculatorText)
            || string.IsNullOrWhiteSpace(checkoutText)
            || !calculatorText.Contains("CalcDisc", StringComparison.Ordinal)
            || !calculatorText.Contains("DescribeOrder", StringComparison.Ordinal)
            || !calculatorText.Contains("\"national\"", StringComparison.Ordinal)
            || !checkoutText.Contains("GetFinalPrice", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        if (Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent)
            && Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent))
        {
            return FinalStateVerdict.Unchanged;
        }

        var calculatorDone = calculatorText.Contains("\"local-pickup\"", StringComparison.Ordinal)
            && calculatorText.Contains("\"regional-courier\"", StringComparison.Ordinal)
            && !calculatorText.Contains("\"local\"", StringComparison.Ordinal)
            && !calculatorText.Contains("\"regional\"", StringComparison.Ordinal);
        var checkoutDone = HasDocCommentAboveGetFinalPrice(checkoutText)
            && checkoutText.Contains("_calculator.CalcDisc(", StringComparison.Ordinal);

        return calculatorDone && checkoutDone ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    private static bool HasDocCommentAboveGetFinalPrice(string text)
    {
        var lines = Normalise(text).Split('\n');
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)
                && lines[i].Contains("Returns the price after discount.", StringComparison.Ordinal)
                && lines[i + 1].Contains("public decimal GetFinalPrice", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
