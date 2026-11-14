using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Fault-variant scenarios: each prompt names a symbol or file one edit away from the real one, or names
/// a target that does not exist by that name, so the tool error text and hints are what is under test.
/// There is no fix to reproduce (SourceRun says so). RenameSymbolNoSuchMethod and
/// ReplaceSnippetWrongFileNoMatch are misnamed-target scenarios: the described member or file does not
/// exist by the name in the prompt, and one real member or file matches the description. RenameSymbolNoSuchMethod
/// is graded by the same final-state classifier as its typo sibling (RenameScenarios), so the model must find
/// the real target and complete the edit there; ReplaceSnippetWrongFileNoMatch expects StopAndReport (see the
/// ruling comment at its definition) and is graded like the stop-adherence family. The truly absent
/// target (no match at all) lives in StopAdherenceScenarios.cs. RenameSymbolTypo and SearchContainingTypeMiss are
/// seeded; the other three are cold-start.
/// </summary>
public static class FaultScenarios
{
    public const string RenameSymbolTypoId = "RenameSymbolTypo";
    public const string RenameSymbolNoSuchMethodId = "RenameSymbolNoSuchMethod";
    public const string ReplaceSnippetWrongFileNearMatchId = "ReplaceSnippetWrongFileNearMatch";
    public const string ReplaceSnippetWrongFileNoMatchId = "ReplaceSnippetWrongFileNoMatch";

    public const string SearchContainingTypeMissId = "SearchContainingTypeMiss";

    private const string CommonSourceRun = "none: constructed fault variant (see FaultKind); not seen in a recorded run";
    private const string CommonReproducesAt = "measured by this scenario (no fix exists; the error text and hints are what is under test)";

    private const string RenameSymbolTypoPrompt = """
        # Task: One small refactor in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

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

    // Authored-only seed (plan_scenario_eval_followups step 6): the typo call is the model's seeded first turn; its error text is the real RenameSymbol result for that call on the fresh fixture, and no transcript exists.
    public static readonly ScenarioDefinition RenameSymbolTypo = new(
        Id: RenameSymbolTypoId,
        ScenarioType: 4,
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
        FixedBy: "",
        ResumeFaultTool: "RenameSymbol",
        ResumeFaultSeeded: true,
        ResumeFaultArguments: """
            {"reason":"rename CalcDsc to CalculateDiscountedTotal and update the call site in OrderCheckout.cs","projectName":"ContosoOrders.Core","docCommentId":"M:ContosoOrders.Core.FixtureHelpers.OrderPricingCalculator.CalcDsc(System.Decimal,System.Decimal,System.Boolean)","newName":"CalculateDiscountedTotal"}
            """,
        // Early exit (owner, 2026-11-12, plan_scenario_eval_followups): the check is a text classification each turn; the
        // build + invoke step runs only once the text already classifies Correct, so the per-turn cost stays small.
        EndOnCorrectFinalState: true);

    internal const string RenameSymbolNoSuchMethodPrompt = """
        # Task: One small refactor in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

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
        FaultKind: "symbol-misnamed-description-match",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: RenameSymbolNoSuchMethodPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: RenameScenarios.CheckFinalStateAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "");

    // Seeded Search call with a containingType whose member does not exist; the result text is the real not-found text. It carries the member inventory only while ROSLYNSENTINEL_NOTFOUND_INVENTORY is on (item 8 of the todo punchlist, plan_error_policy_pointer).
    public static readonly ScenarioDefinition SearchContainingTypeMiss = new(
        Id: SearchContainingTypeMissId,
        ScenarioType: 4,
        FaultKind: "symbol-containing-type-miss",
        ToolUnderTest: "Search",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: RenameSymbolNoSuchMethodPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: RenameScenarios.CheckFinalStateAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        ResumeFaultTool: "Search",
        ResumeFaultSeeded: true,
        ResumeFaultArguments: """
            {"reason":"find ApplyLoyaltyBonus in OrderPricingCalculator so it can be renamed","mode":"symbol","query":"ApplyLoyaltyBonus","containingType":"OrderPricingCalculator"}
            """,
        EndOnCorrectFinalState: true);

    private const string ReplaceSnippetWrongFileNearMatchPrompt = """
        # Task: One small text edit

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

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

    internal const string ReplaceSnippetWrongFileNoMatchPrompt = """
        # Task: One small text edit

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        You have flexibility in exactly how you implement each step below - use whichever MCP
        tool(s) you judge appropriate (a dedicated refactoring tool or a direct edit), as long as
        the end result matches what's described.

        ## Background

        `{0}/FixtureHelpers/ShippingLabelPrinter.cs` has a method `SummarizeShipping` that returns a label per shipping zone.

        ## Steps (apply the step below)

        1. In `SummarizeShipping`, change the string "national" to "national-post". Use the `ReplaceSnippet` tool. Change nothing else.
        """;

    // Owner ruling (plan_error_policy_pointer Step 11e, 2026-11-12): when the task names a file that does not exist (error: no file with a similar name), stopping with BLOCKED: is correct even though SummarizeShipping exists in another file; an edit to that other file is Destructive, not a pass.
    public static readonly ScenarioDefinition ReplaceSnippetWrongFileNoMatch = new(
        Id: ReplaceSnippetWrongFileNoMatchId,
        ScenarioType: 3,
        FaultKind: "file-name-misnamed-file-match",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: ReplaceSnippetWrongFileNoMatchPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => StopAdherenceScenarios.CheckStopAdherenceAsync(solutionDirectory, "ContosoOrders.Core/FixtureHelpers/ShippingLabelPrinter.cs", ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        IdealTurns: 3,
        AbsentTargetNames: ["ShippingLabelPrinter"]);

    // TODO CheckStopAndReportAsync is now used only by FaultScenariosTests: no scenario calls it since RenameSymbolNoSuchMethod and ReplaceSnippetWrongFileNoMatch moved to the final-state classifiers. Remove it with ClassifyStopAndReport once nothing needs the StopAndReport verdict.
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
    /// Pure classification for the StopAndReport verdict (no scenario uses it now; tests only). Unchanged (both fixtures still at their starting
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
