using System.Text.RegularExpressions;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Regression guard scenarios for two fixes that are on by default: twin apply calls in one turn
/// (action aliases and identical-resend no-op) and a whole-file ReplaceSnippet edit that would empty
/// a file (refused with TargetIneligible). Both are expected to recover, not to reproduce.
/// Both scenarios live in this one class, so their classify and check methods have distinct names.
/// </summary>
public static class GuardScenarios
{
    public const string TwinApplyCallsId = "TwinApplyCalls";
    public const string EmptyInterfaceRemovalId = "EmptyInterfaceRemoval";

    private static readonly Regex InterfaceDeclaration = new(@"\binterface\s+IOrderPricingCalculator\b", RegexOptions.CultureInvariant);

    private const string TwinApplyCallsPrompt = """
        # Task: One small text edit

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `SummarizeShipping` that returns a label per shipping zone.

        ## Steps (apply the step below)

        1. In `SummarizeShipping`, change the string "national" to "national-post". Use the `ReplaceSnippet` tool. Change nothing else.
        """;

    public static readonly ScenarioDefinition TwinApplyCalls = new(
        Id: TwinApplyCallsId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: TwinApplyCallsPrompt,
        TurnCap: 6,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReplaceSnippet",
        CheckFinalState: CheckTwinApplyCallsFinalStateAsync,
        SourceRun: "10-chain batch F run 20261111-042157-571 (twin replace/apply ReplaceSnippet calls in one turn at T2, T4, T7, T12, T18, T19) and batch H4 run 20261111-193910-723; see docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md",
        ReproducesAt: "not measured (regression guard)",
        FixedBy: "action value replace/edit/write aliased to apply e303b7d1; identical resend returns a no-op success 68fef93b (both default on)");

    /// <summary>
    /// Reads the calculator and checkout after a twin-apply run and returns the verdict. The build check
    /// runs only when the files classify as Correct; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckTwinApplyCallsFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, cancellationToken);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);

        var verdict = ClassifyTwinApplyCallsFiles(calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        var built = await FunctionalFixVerifier.BuildSucceedsAsync(ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), cancellationToken);
        return built ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    /// <summary>Pure classification of the twin-apply fixture texts (null when the file is missing).</summary>
    public static FinalStateVerdict ClassifyTwinApplyCallsFiles(string? calculatorText, string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(calculatorText)
            || string.IsNullOrWhiteSpace(checkoutText)
            || !calculatorText.Contains("CalcDisc", StringComparison.Ordinal)
            || !calculatorText.Contains("DescribeOrder", StringComparison.Ordinal)
            || !calculatorText.Contains("SummarizeShipping", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        var calculatorStarting = Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent);
        var checkoutStarting = Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent);
        if (calculatorStarting && checkoutStarting)
        {
            return FinalStateVerdict.Unchanged;
        }

        var calculatorDone = CountOccurrences(calculatorText, "\"national-post\"") == 1
            && !calculatorText.Contains("\"national\"", StringComparison.Ordinal)
            && calculatorText.Contains("\"local\"", StringComparison.Ordinal)
            && calculatorText.Contains("\"regional\"", StringComparison.Ordinal)
            && CountOccurrences(calculatorText, "SummarizeShipping") == 1;

        return calculatorDone && checkoutStarting ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    private const string EmptyInterfaceRemovalPrompt = """
        # Task: Remove an unused interface

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/IOrderPricingCalculator.cs` declares the interface `IOrderPricingCalculator`. Nothing implements or uses it.

        ## Steps (apply the step below)

        1. Remove the interface `IOrderPricingCalculator` from the solution. Use the `ReplaceSnippet` tool. Change nothing else.
        """;

    // Declared before EmptyInterfaceRemoval: static field initializers run in textual order.
    private static readonly IReadOnlyDictionary<string, string> EmptyInterfaceRemovalStagedFiles =
        new Dictionary<string, string>(ScenarioFixtureFiles.ChainStartingFiles)
        {
            [InterfaceScenarios.InterfaceRelativePath] = InterfaceScenarios.InterfaceStartingContent,
        };

    public static readonly ScenarioDefinition EmptyInterfaceRemoval = new(
        Id: EmptyInterfaceRemovalId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: EmptyInterfaceRemovalStagedFiles,
        PromptTemplate: EmptyInterfaceRemovalPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: CheckEmptyInterfaceRemovalFinalStateAsync,
        SourceRun: "10-chain batch E run 20261110-220441-748 turn 21 (whole-file oldContent with empty newContent left an interface file empty, then 10 alternating 'oldContent is required' / 'newContent is required' errors); see docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md",
        ReproducesAt: "not measured (regression guard; the cold prompt is a judgement of the smallest reproduction)",
        FixedBy: "cb7dc620 (a ReplaceSnippet edit that would leave a file with no type declaration is refused with TargetIneligible; default on)");

    /// <summary>
    /// Reads the interface, calculator and checkout after an empty-interface-removal run and returns the
    /// verdict. The build check runs only when the files classify as Correct; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckEmptyInterfaceRemovalFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var interfaceText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, InterfaceScenarios.InterfaceRelativePath, cancellationToken);
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, cancellationToken);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);

        var verdict = ClassifyEmptyInterfaceRemovalFiles(interfaceText, calculatorText, checkoutText);
        if (verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        var built = await FunctionalFixVerifier.BuildSucceedsAsync(ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), cancellationToken);
        return built ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    /// <summary>Pure classification of the empty-interface-removal fixture texts (null when the file is missing).</summary>
    public static FinalStateVerdict ClassifyEmptyInterfaceRemovalFiles(string? interfaceText, string? calculatorText, string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(calculatorText)
            || string.IsNullOrWhiteSpace(checkoutText)
            || !calculatorText.Contains("DescribeOrder", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        var calculatorStarting = Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent);
        var checkoutStarting = Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        if (interfaceText is not null
            && Normalise(interfaceText) == Normalise(InterfaceScenarios.InterfaceStartingContent)
            && calculatorStarting
            && checkoutStarting)
        {
            return FinalStateVerdict.Unchanged;
        }

        var interfaceRemoved = interfaceText is null || !InterfaceDeclaration.IsMatch(interfaceText);
        return interfaceRemoved && calculatorStarting && checkoutStarting ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    private static int CountOccurrences(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
