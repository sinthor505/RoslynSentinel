using System.Text.RegularExpressions;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Targeted scenario: add one member to an interface that a run must widen without re-declaring
/// anything already present. Regression guard for the CS0111 re-declaration seen in the ten-chain
/// batch G run (plan_targeted_tool_scenario_evals).
/// </summary>
public static class InterfaceScenarios
{
    public const string InterfaceMemberRedeclaredId = "InterfaceMemberRedeclared";
    public const string InterfaceRelativePath = "ContosoOrders.Core/FixtureHelpers/IOrderPricingCalculator.cs";

    public const string InterfaceStartingContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public interface IOrderPricingCalculator
        {
            decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer);
        }
        """;

    private static readonly Regex DescribeOrderDeclaration = new(@"\bstring\s+DescribeOrder\s*\(\s*int\s+\w+\s*,\s*string\s+\w+\s*\)\s*;", RegexOptions.CultureInvariant);
    private static readonly Regex CalcDiscCall = new(@"\bCalcDisc\s*\(", RegexOptions.CultureInvariant);

    // Declared before InterfaceMemberRedeclared: static field initializers run in textual order.
    private static readonly IReadOnlyDictionary<string, string> StagedWithInterfaceFiles =
        new Dictionary<string, string>(ScenarioFixtureFiles.ChainStartingFiles)
        {
            [InterfaceRelativePath] = InterfaceStartingContent,
        };

    private const string InterfaceMemberRedeclaredPrompt = """
        # Task: Add one member to an interface

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/IOrderPricingCalculator.cs` declares the interface `IOrderPricingCalculator` with one member, `CalcDisc`.

        ## Steps (apply the step below)

        1. **Widen the interface**: add the member `string DescribeOrder(int id, string label);` to `IOrderPricingCalculator`, below the existing member. Do not add anything else to the interface (in particular not `SummarizeShipping`), and do not change any other file.
        """;

    public static readonly ScenarioDefinition InterfaceMemberRedeclared = new(
        Id: InterfaceMemberRedeclaredId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: StagedWithInterfaceFiles,
        PromptTemplate: InterfaceMemberRedeclaredPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReplaceSnippet",
        CheckFinalState: CheckFinalStateAsync,
        SourceRun: "10-chain batch G run 20261111-074907-352, turns 12-24 (interface member re-declared, CS0111, then identical resends); see docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md",
        ReproducesAt: "not measured (regression guard; the cold-start prompt is a judgement of the smallest reproduction, the original needed the member to have been added a turn earlier)",
        FixedBy: "CS0111 same-file hint 000af724 and repeat note 31b206a6 (both default on)");

    /// <summary>
    /// Reads the interface, calculator and checkout after a run and returns the verdict. The build check
    /// runs only when the files classify as Correct; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var interfaceText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, InterfaceRelativePath, cancellationToken);
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, cancellationToken);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);

        var verdict = ClassifyFiles(interfaceText, calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        var built = await FunctionalFixVerifier.BuildSucceedsAsync(ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), cancellationToken);
        return built ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    /// <summary>Pure classification of the three fixture texts (null when the file is missing).</summary>
    public static FinalStateVerdict ClassifyFiles(string? interfaceText, string? calculatorText, string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(interfaceText)
            || string.IsNullOrWhiteSpace(calculatorText)
            || string.IsNullOrWhiteSpace(checkoutText)
            || !interfaceText.Contains("CalcDisc", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        var calculatorStarting = Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent);
        var checkoutStarting = Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        if (Normalise(interfaceText) == Normalise(InterfaceStartingContent) && calculatorStarting && checkoutStarting)
        {
            return FinalStateVerdict.Unchanged;
        }

        if (DescribeOrderDeclaration.Matches(interfaceText).Count == 1
            && CalcDiscCall.Matches(interfaceText).Count == 1
            && !interfaceText.Contains("SummarizeShipping", StringComparison.Ordinal)
            && calculatorStarting
            && checkoutStarting)
        {
            return FinalStateVerdict.Correct;
        }

        return FinalStateVerdict.Wrong;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
