using System.IO;
using RoslynSentinel.Common;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Resume scenarios (type 2): history is seeded from a tracked capture in ResumeTranscripts and the live
/// model continues from there. The first one resumes a ten-chain run (turns 1-11) after a RenameSymbol at turn 5 that
/// already rewrote the OrderCheckout.cs call site; the failure it models is a later ReplaceSnippet whose oldContent still names the old method.
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
        SourceRun: "10-chain run 20261114-082018-100 (host 113, qwen3.5-9b-mtp, lean tool surface, commit 70b436d5), turns 1-11: RenameSymbol CalcDisc to CalculateDiscountedTotal at turn 5 (it rewrote the OrderCheckout.cs call site), OrderCheckout.cs otherwise untouched; the NotFound failure it models (ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc) was seen at turn 12 of the earlier 55-tool run 20261112-031134-405 and is not part of this capture",
        ReproducesAt: "not measured (regression guard; the failure was seen at turn 12 of run 20261112-031134-405, resume rate measured by this scenario)",
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
        SourceRun: "stale workspace variant of: 10-chain run 20261114-082018-100 (host 113, lean tool surface, commit 70b436d5), turns 1-11, RenameSymbol at turn 5; original failure: ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc after the rename, NotFound (run 20261112-031134-405 turn 12)",
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
        SourceRun: "authored failed-last-call variant of: 10-chain run 20261114-082018-100 (host 113, lean tool surface, commit 70b436d5), turns 1-11, RenameSymbol at turn 5; original failure: ReplaceSnippet on OrderCheckout.cs with oldContent still naming CalcDisc after the rename, NotFound (run 20261112-031134-405 turn 12)",
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
    /// Path of a file in the tracked RoslynSentinel.Tests.ModelEval/ResumeTranscripts folder. The repo root comes from
    /// RepoRootLocator, which reads ROSLYNSENTINEL_REPO_ROOT, then walks up from the test binary folder. The file itself is not checked.
    /// </summary>
    public static string CapturePath(string fileName)
    {
        var root = RepoRootLocator.Find();
        return Path.Combine(root, "RoslynSentinel.Tests.ModelEval", "ResumeTranscripts", fileName);
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
