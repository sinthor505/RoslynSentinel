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

    /// <summary>Starting interface file for EmptyInterfaceRemoval: the calculator interface plus an unreferenced second type.</summary>
    public const string EmptyInterfaceRemovalStartingContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public interface IOrderPricingCalculator
        {
            decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer);
        }

        public sealed record PricingAuditEntry(string Label, decimal Amount);
        """;

    public const string EmptyInterfaceRemovalOtherTypeName = "PricingAuditEntry";

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

    /// <summary>The seeded apply call: the same ReplaceSnippet edit is sent twice in the seeded turn (see TwinApplyCalls).</summary>
    internal const string TwinApplyCallsArguments = """
        {"reason":"change national to national-post in SummarizeShipping","action":"apply","filePath":"{SOLUTION}/ContosoOrders.Core/FixtureHelpers/OrderPricingCalculator.cs","oldContent":"_ => \"national\",","newContent":"_ => \"national-post\","}
        """;

    // Authored-only seed (plan_scenario_eval_followups step 7, Decision 11): the model's seeded first turn holds TWO identical
    // ReplaceSnippet calls, the original defect (twin calls in one turn, see SourceRun). Both are executed on the fresh fixture
    // and the model sees their real results: the first applies, the second hits the duplicate-resend no-op and returns a
    // SUCCESS (isError false): 'No change: this exact edit was already applied by an earlier ReplaceSnippet call (changeId <id>),
    // and the file already contains your newContent. Do not send it again; continue with the next step.' (measured by
    // ResumeFaultExecutionTests.ApplyResumeFaultCalls_TwinApplyCallsScenario_SecondIdenticalCallIsANoOpSuccess). So the question
    // under test is the model's reaction, graded by ClassifyTwinApplyReaction (MovedOn / ResentAgain / Reverted / Damaged) plus the existing
    // final-state check. ScenarioType is 2 because the seeded turn contains no tool error.
    public static readonly ScenarioDefinition TwinApplyCalls = new(
        Id: TwinApplyCallsId,
        ScenarioType: 2,
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
        FixedBy: "action value replace/edit/write aliased to apply e303b7d1; identical resend returns a no-op success 68fef93b (both default on)",
        ResumeFaultTool: "ReplaceSnippet",
        ResumeFaultSeeded: true,
        ResumeFaultArguments: TwinApplyCallsArguments,
        ResumeFaultExtraCalls: [("ReplaceSnippet", TwinApplyCallsArguments)]);

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

    /// <summary>What the model did after the seeded twin apply calls; per sample, see ClassifyTwinApplyReaction.</summary>
    public enum TwinApplyReaction
    {
        /// <summary>Final state Correct and the identical call was sent fewer than ResentAgainRepeatThreshold times.</summary>
        MovedOn,

        /// <summary>Sent the identical call a third time or more (the seeded turn already holds 2), whatever the verdict.</summary>
        ResentAgain,

        /// <summary>Final state Unchanged: the file is back at its starting text, so the seeded edit was undone.</summary>
        Reverted,

        /// <summary>Final state Wrong or Destructive (re-applied in another form, or breakage), or no final-state verdict.</summary>
        Damaged,
    }

    /// <summary>Identical-call count at which a sample counts as ResentAgain (the seeded turn already holds 2).</summary>
    public const int ResentAgainRepeatThreshold = 3;

    /// <summary>
    /// Classifies the reaction to the seeded twin apply calls. Rules in order: a repeat of at least ResentAgainRepeatThreshold
    /// is ResentAgain, whatever the verdict; then verdict Correct is MovedOn, Unchanged is Reverted, and Wrong or Destructive is
    /// Damaged. A null verdict is also Damaged, on purpose: with a repeat below the threshold and no verdict the run cannot be
    /// called MovedOn, and Damaged is the conservative bucket that never overstates success. Usable from rescoring, or on
    /// results.csv rows parsed with ScenarioSampleCsv (columns maxIdenticalCallRepeat and finalStateVerdict). The eval doc applies
    /// it per sample; there is no new CSV column.
    /// </summary>
    public static TwinApplyReaction ClassifyTwinApplyReaction(int maxIdenticalCallRepeat, FinalStateVerdict? verdict)
    {
        if (maxIdenticalCallRepeat >= ResentAgainRepeatThreshold)
        {
            return TwinApplyReaction.ResentAgain;
        }

        return verdict switch
        {
            FinalStateVerdict.Correct => TwinApplyReaction.MovedOn,
            FinalStateVerdict.Unchanged => TwinApplyReaction.Reverted,
            _ => TwinApplyReaction.Damaged,
        };
    }

    /// <summary>
    /// Classifies one scored run from its MaxIdenticalCallRepeat and Verdict (see the int/verdict overload). A null Verdict
    /// maps to Damaged, as there.
    /// </summary>
    public static TwinApplyReaction ClassifyTwinApplyReaction(ScoreResult score) =>
        ClassifyTwinApplyReaction(score.MaxIdenticalCallRepeat, score.Verdict);

    /// <summary>
    /// Classifies one results row. Still null when the sample is inconclusive (Score is null), since there is no run to read;
    /// otherwise the ScoreResult overload, so a null Verdict maps to Damaged.
    /// </summary>
    public static TwinApplyReaction? ClassifyTwinApplyReaction(ScenarioSample sample) =>
        sample.Score is null ? (TwinApplyReaction?)null : ClassifyTwinApplyReaction(sample.Score);

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
            [InterfaceScenarios.InterfaceRelativePath] = EmptyInterfaceRemovalStartingContent,
        };

    // TargetIneligible empty-file guard (cb7dc620) reach for this scenario: only a ReplaceSnippet whole-content
    // replace whose result would leave the file with no type declaration is refused. On this two-type file that
    // means replacing the entire content with empty text. Removing just the interface snippet leaves
    // PricingAuditEntry, so the guard does not fire. DeleteFile never hits the guard and is graded Wrong (it
    // removes the other type too). A whole-file replace that keeps the other type does not hit the guard either.
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

    /// <summary>
    /// Pure classification of the empty-interface-removal fixture texts (null when the file is missing).
    /// A deleted file is Wrong because it removes a type the task did not name (PricingAuditEntry).
    /// </summary>
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

        if (interfaceText is null)
        {
            return FinalStateVerdict.Wrong;
        }

        if (Normalise(interfaceText) == Normalise(EmptyInterfaceRemovalStartingContent)
            && calculatorStarting
            && checkoutStarting)
        {
            return FinalStateVerdict.Unchanged;
        }

        if (!interfaceText.Contains(EmptyInterfaceRemovalOtherTypeName, StringComparison.Ordinal))
        {
            return FinalStateVerdict.Wrong;
        }

        if (InterfaceDeclaration.IsMatch(interfaceText))
        {
            return FinalStateVerdict.Wrong;
        }

        return calculatorStarting && checkoutStarting ? FinalStateVerdict.Correct : FinalStateVerdict.Wrong;
    }

    private static int CountOccurrences(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
