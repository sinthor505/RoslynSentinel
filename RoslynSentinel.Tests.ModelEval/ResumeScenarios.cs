using System.IO;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Resume scenarios (type 2): history is seeded from a tracked capture in ResumeTranscripts and the live
/// model continues from there. The first one resumes a ten-chain run after a RenameSymbol at turn 4 whose
/// later ReplaceSnippet still named the old method (NotFound at turn 12 of the capture run).
/// </summary>
public static class ResumeScenarios
{
    public const string StaleCallerEditAfterRenameId = "StaleCallerEditAfterRename";

    public const string StaleWorkspaceAfterRenameId = "StaleWorkspaceAfterRename";

    public const string FailedLastCallStaleOldContentId = "FailedLastCallStaleOldContent";

    // Declared before StaleCallerEditAfterRename: static field initializers run in textual order.
    private static readonly Regex InterfaceTypedCalculatorField = new(@"\bIOrderPricingCalculator\s+_calculator\b", RegexOptions.CultureInvariant);
    private static readonly Regex ConcreteTypedCalculatorField = new(@"\bOrderPricingCalculator\s+_calculator\b", RegexOptions.CultureInvariant);

    public static readonly ScenarioDefinition StaleCallerEditAfterRename = new(
        Id: StaleCallerEditAfterRenameId,
        ScenarioType: 2,
        FaultKind: "",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: "",
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReadFile",
        CheckFinalState: CheckStaleCallerEditFinalStateAsync,
        SourceRun: "10-chain run 20261112-031134-405 (plan_targeted_tool_scenario_evals batch C, commit c7bddd9e) turn 12: ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc after the turn 4 RenameSymbol, NotFound",
        ReproducesAt: "not measured (regression guard; the capture run hit it at turn 12, resume rate measured by this scenario)",
        FixedBy: "",
        ResumeTranscript: "stale-caller-edit-after-rename.json",
        ResumeKeepTurns: 11,
        EndOnCorrectFinalState: true);

    public static readonly ScenarioDefinition StaleWorkspaceAfterRename = new(
        Id: StaleWorkspaceAfterRenameId,
        ScenarioType: 4,
        FaultKind: "stale-workspace: after the replay OrderCheckout.cs has its calculator field initializer rewritten (new() -> new OrderPricingCalculator()) by an edit the conversation never shows; a ReplaceSnippet whose oldContent copies the field line from the earlier ReadFile comes back NotFound",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: "",
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReadFile",
        CheckFinalState: CheckStaleCallerEditFinalStateAsync,
        SourceRun: "stale workspace variant of: 10-chain run 20261112-031134-405 (plan_targeted_tool_scenario_evals batch C, commit c7bddd9e) turn 12: ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc after the turn 4 RenameSymbol, NotFound",
        ReproducesAt: "not measured (resume faulted scenario, rate measured by this scenario)",
        FixedBy: "",
        ResumeTranscript: "stale-caller-edit-after-rename.json",
        ResumeKeepTurns: 11,
        ResumeFaultTool: "ReplaceSnippet",
        ResumeFaultSeeded: false,
        ResumeFaultArguments: """
            {"reason":"stale workspace fault: another session rewrote the calculator field initializer","action":"apply","filePath":"{SOLUTION}/ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs","oldContent":"private readonly OrderPricingCalculator _calculator = new();","newContent":"private readonly OrderPricingCalculator _calculator = new OrderPricingCalculator();"}
            """,
        EndOnCorrectFinalState: true);

    public static readonly ScenarioDefinition FailedLastCallStaleOldContent = new(
        Id: FailedLastCallStaleOldContentId,
        ScenarioType: 4,
        FaultKind: "authored failed last call: after the replay one assistant ReplaceSnippet turn is appended to the history (authored by the harness, not the model) whose oldContent still names CalcDisc, which the turn 4 rename removed; the NotFound result shown to the model is the real server result",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: "",
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: "ReadFile",
        CheckFinalState: CheckStaleCallerEditFinalStateAsync,
        SourceRun: "authored failed-last-call variant of: 10-chain run 20261112-031134-405 (plan_targeted_tool_scenario_evals batch C, commit c7bddd9e) turn 12: ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc after the turn 4 RenameSymbol, NotFound",
        ReproducesAt: "not measured (resume faulted scenario, rate measured by this scenario)",
        FixedBy: "",
        ResumeTranscript: "stale-caller-edit-after-rename.json",
        ResumeKeepTurns: 11,
        ResumeFaultTool: "ReplaceSnippet",
        ResumeFaultSeeded: true,
        ResumeFaultArguments: """
            {"reason":"update the caller in OrderCheckout.cs to the renamed method","action":"apply","filePath":"{SOLUTION}/ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs","oldContent":"return _calculator.CalcDisc(amount, rate, isPreferredCustomer);","newContent":"return _calculator.CalculateDiscountedTotal(amount, rate, isPreferredCustomer);"}
            """,
        EndOnCorrectFinalState: true);

    /// <summary>
    /// Path of a file in the tracked RoslynSentinel.Tests.ModelEval/ResumeTranscripts folder. Walks up from the
    /// test binary folder to the directory holding RoslynSentinel.slnx. The file itself is not checked.
    /// </summary>
    public static string CapturePath(string fileName)
    {
        var start = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RoslynSentinel.slnx")))
            {
                return Path.Combine(directory.FullName, "RoslynSentinel.Tests.ModelEval", "ResumeTranscripts", fileName);
            }
        }

        throw new InvalidOperationException($"No RoslynSentinel.slnx found walking up from '{start}'.");
    }

    /// <summary>
    /// Reads OrderCheckout.cs after the resumed run and returns the verdict. No build runs here: the
    /// caller edit is only half of the chain's step 8 at this point.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckStaleCallerEditFinalStateAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, cancellationToken);
        return ClassifyCheckout(checkoutText);
    }

    /// <summary>
    /// Pure classification of OrderCheckout.cs text (null when the file is missing). Correct means the field is
    /// typed as the interface and the call already uses the renamed method.
    /// </summary>
    public static FinalStateVerdict ClassifyCheckout(string? checkoutText)
    {
        if (string.IsNullOrWhiteSpace(checkoutText) || !checkoutText.Contains("GetFinalPrice", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Destructive;
        }

        if (checkoutText.Contains("CalcDisc", StringComparison.Ordinal)
            || !checkoutText.Contains("CalculateDiscountedTotal(", StringComparison.Ordinal))
        {
            return FinalStateVerdict.Wrong;
        }

        if (InterfaceTypedCalculatorField.IsMatch(checkoutText))
        {
            return FinalStateVerdict.Correct;
        }

        return ConcreteTypedCalculatorField.IsMatch(checkoutText) ? FinalStateVerdict.Unchanged : FinalStateVerdict.Wrong;
    }
}
