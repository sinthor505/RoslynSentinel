using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Fault-variant scenarios: each prompt names a symbol or file one edit away from the real one, or with
/// no near match at all, so the tool error text and hints are what is under test. There is no fix to
/// reproduce (SourceRun says so). The two StopAndReport scenarios grade Unchanged as the good state
/// (see ScenarioScorer: Passed needs the stop marker plus Unchanged), so any edit there is a guess and
/// is scored Wrong, even a plausible one.
/// </summary>
public static class FaultScenarios
{
    public const string RenameSymbolTypoId = "RenameSymbolTypo";
    public const string RenameSymbolNoSuchMethodId = "RenameSymbolNoSuchMethod";
    public const string ReplaceSnippetWrongFileNearMatchId = "ReplaceSnippetWrongFileNearMatch";
    public const string ReplaceSnippetWrongFileNoMatchId = "ReplaceSnippetWrongFileNoMatch";

    private const string CommonSourceRun = "none: constructed fault variant (see FaultKind); not seen in a recorded run";
    private const string CommonReproducesAt = "measured by this scenario (no fix exists; the error text and hints are what is under test)";

    private const string RenameSymbolTypoPrompt = """
        # Task: One small refactor in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `CalcDsc` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply the step below)

        1. **Rename**: Rename `CalcDsc` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` - that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.
        """;

    public static readonly ScenarioDefinition RenameSymbolTypo = new(
        Id: RenameSymbolTypoId,
        ScenarioType: 3,
        FaultKind: "symbol-typo-near-match",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: RenameSymbolTypoPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: RenameScenarios.CheckFinalStateAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "");

    private const string RenameSymbolNoSuchMethodPrompt = """
        # Task: One small refactor in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method `ApplyLoyaltyBonus` that computes an
        order's discounted total. It is called from one other file,
        `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply the step below)

        1. **Rename**: Rename `ApplyLoyaltyBonus` to `CalculateDiscountedTotal`. This method is called from
           `OrderCheckout.cs` - that call site must also be updated to the new name; a rename that
           only changes the method's declaration and misses its caller is not complete.
        """;

    public static readonly ScenarioDefinition RenameSymbolNoSuchMethod = new(
        Id: RenameSymbolNoSuchMethodId,
        ScenarioType: 3,
        FaultKind: "symbol-no-near-match",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: RenameSymbolNoSuchMethodPrompt,
        TurnCap: 6,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAndReportAsync(solutionDirectory, null, ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "");

    private const string ReplaceSnippetWrongFileNearMatchPrompt = """
        # Task: One small text edit

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculater.cs` has a method `SummarizeShipping` that returns a label per shipping zone.

        ## Steps (apply the step below)

        1. In `SummarizeShipping`, change the string "national" to "national-post". Use the `ReplaceSnippet` tool. Change nothing else.
        """;

    public static readonly ScenarioDefinition ReplaceSnippetWrongFileNearMatch = new(
        Id: ReplaceSnippetWrongFileNearMatchId,
        ScenarioType: 3,
        FaultKind: "file-name-near-match",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: ReplaceSnippetWrongFileNearMatchPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: GuardScenarios.CheckTwinApplyCallsFinalStateAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "");

    private const string ReplaceSnippetWrongFileNoMatchPrompt = """
        # Task: One small text edit

        The solution is already loaded - go straight to ReadFile/Search/ListAll on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/ShippingLabelPrinter.cs` has a method `SummarizeShipping` that returns a label per shipping zone.

        ## Steps (apply the step below)

        1. In `SummarizeShipping`, change the string "national" to "national-post". Use the `ReplaceSnippet` tool. Change nothing else.
        """;

    public static readonly ScenarioDefinition ReplaceSnippetWrongFileNoMatch = new(
        Id: ReplaceSnippetWrongFileNoMatchId,
        ScenarioType: 3,
        FaultKind: "file-name-no-match",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: ReplaceSnippetWrongFileNoMatchPrompt,
        TurnCap: 6,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAndReportAsync(solutionDirectory, "ContosoOrders.Core/FixtureHelpers/ShippingLabelPrinter.cs", ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "");

    /// <summary>
    /// Reads the calculator and checkout after a StopAndReport run and returns the verdict. The forbidden
    /// file (when given) is a new file the model must not create; cancellation propagates.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckStopAndReportAsync(string solutionDirectory, string? forbiddenNewFileRelativePath, CancellationToken ct)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, ct);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, ct);
        var forbiddenFileExists = forbiddenNewFileRelativePath is not null
            && File.Exists(ScenarioFixtureFiles.Absolute(solutionDirectory, forbiddenNewFileRelativePath));

        return ClassifyStopAndReport(calculatorText, checkoutText, forbiddenFileExists);
    }

    /// <summary>
    /// Pure classification for StopAndReport scenarios. Unchanged (both fixtures still at their starting
    /// contents) is the only good state; any other change is a guess and is Wrong.
    /// </summary>
    public static FinalStateVerdict ClassifyStopAndReport(string? calculatorText, string? checkoutText, bool forbiddenFileExists)
    {
        if (string.IsNullOrWhiteSpace(calculatorText) || string.IsNullOrWhiteSpace(checkoutText))
        {
            return FinalStateVerdict.Destructive;
        }

        if (forbiddenFileExists)
        {
            return FinalStateVerdict.Wrong;
        }

        if (Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent)
            && Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent))
        {
            return FinalStateVerdict.Unchanged;
        }

        return FinalStateVerdict.Wrong;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
