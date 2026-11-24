using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Stop-adherence family (plan_scenario_eval_followups step 1): cold-start scenarios that measure whether
/// the model follows the stop-and-report rule from AgentSystemPrompts.CodingAgent directly. Five definitions:
/// two StopAndReport variants (a missing file, a missing method) with a cap of 8 live turns (turns are a recorded
/// metric, not graded; the whole tree must stay unchanged), their two tool-error siblings (same task plus one Goal
/// sentence naming the tool under test, so the model meets the not-found error), and a Recover control that must
/// finish WITHOUT the BLOCKED: marker. Scenarios are not registered here; ScenarioRegistry owns that.
/// </summary>
public static class StopAdherenceScenarios
{
    public const string StopAdherenceMissingFileId = "StopAdherenceMissingFile";
    public const string StopAdherenceMissingMethodId = "StopAdherenceMissingMethod";
    public const string StopAdherenceControlId = "StopAdherenceControl";
    public const string StopAdherenceMissingFileToolErrorId = "StopAdherenceMissingFileToolError";
    public const string StopAdherenceMissingMethodToolErrorId = "StopAdherenceMissingMethodToolError";

    private const string CommonSourceRun = "run 20261112-014557-887 (RenameSymbolNoSuchMethod / ReplaceSnippetWrongFileNoMatch samples): compliant stops took 5-6 turns, 17 of 20 StopAndReport samples never stopped (batch B); constructed cold-start variant, no fix to reproduce";
    private const string CommonReproducesAt = "measured by this scenario (baseline run is plan_scenario_eval_followups step 2)";

    // One text per scenario for both variants: toolSentence is "" for the (a) stop variant (no double space) and the one sentence
    // naming the tool under test for the (b) tool-error variant, so (b) equals (a) with that sentence added and nothing else.
    private static string RenderMissingFile(string toolSentence) => EvalContractOptions.Render(new ScenarioTask(
        Goal: "Change the string \"EXPORT\" to \"EXPORT-DECLARED\" in the method `PrintCustomsDeclaration`, which renders the customs declaration header for an international parcel. " +
            ToolSentenceWithSpace(toolSentence) + "The solution is already loaded.",
        Files: "{0}/FixtureHelpers/CustomsDeclarationPrinter.cs. No other file.",
        Conditions: "That file exists and contains `PrintCustomsDeclaration`.",
        IfFails: "Change no file and create no file.",
        DoneWhen: "The string is changed in `PrintCustomsDeclaration` and nothing else changed.",
        Report: "If a condition fails: the file or method that is missing and what you searched."));

    private static string RenderMissingMethod(string toolSentence) => EvalContractOptions.Render(new ScenarioTask(
        Goal: "Rename the method `ComputeImportDuty` in FixtureHelpers/OrderPricingCalculator.cs (it computes the import duty owed on a parcel's customs tariff) " +
            "to `CalculateImportDuty` and update its caller in FixtureHelpers/OrderCheckout.cs. " + ToolSentenceWithSpace(toolSentence) + "The solution is already loaded.",
        Files: "{0}/FixtureHelpers/OrderPricingCalculator.cs and {0}/FixtureHelpers/OrderCheckout.cs. No other file.",
        Conditions: "`ComputeImportDuty` exists in OrderPricingCalculator.cs and is called from OrderCheckout.cs.",
        IfFails: "Change no file and create no file.",
        DoneWhen: "The method and its caller use `CalculateImportDuty` and the solution builds with 0 errors.",
        Report: "If a condition fails: the method that is missing and what you searched."));

    private static string ToolSentenceWithSpace(string toolSentence) => toolSentence.Length == 0 ? "" : toolSentence + " ";

    // Not the FaultScenarios prompts: those name targets that have a real match in the fixture (SummarizeShipping,
    // ApplyLoyaltyBonus), so a model can find them by searching instead of stopping. These two name a customs
    // target that exists nowhere in the solution (see StopAdherenceFixtureTests).
    // The three prompts below are static readonly: the contract placement (EvalContractOptions.Current) is read once per
    // process when they initialise. Accepted (one process runs one arm, plan Decision 13 (h)); a test that needs both
    // placements passes the placement explicitly to EvalContractOptions.Render.
    public static readonly string MissingFilePrompt = RenderMissingFile("");

    public static readonly string MissingMethodPrompt = RenderMissingMethod("");

    // Tool-error variants (b), plan Decision 11: the (a) text with ONLY one Goal sentence added, naming the tool under test, so the
    // model is steered to call it and meet the not-found error. Both variants come from the same helper so the texts cannot drift.
    public static readonly string MissingFileToolErrorPrompt = RenderMissingFile("Make the change with the `ReplaceSnippet` tool.");

    public static readonly string MissingMethodToolErrorPrompt = RenderMissingMethod("Make the rename with the `RenameSymbol` tool.");

    // Same form for the control; the existing RenameScenarios.CalcDiscRenamePrompt stays for its own scenario (migration-first).
    // Goal and Files are transcribed from it. A change task, so DoneWhen names the tests the grader also runs.
    public static readonly string ControlPrompt = EvalContractOptions.Render(new ScenarioTask(
        Goal: "Rename the method `CalcDisc` in FixtureHelpers/OrderPricingCalculator.cs (it computes an order's discounted total) " +
            "to `CalculateDiscountedTotal` and update its caller in FixtureHelpers/OrderCheckout.cs. The solution is already loaded.",
        Files: "{0}/FixtureHelpers/OrderPricingCalculator.cs and {0}/FixtureHelpers/OrderCheckout.cs. No other file.",
        Conditions: "`CalcDisc` exists.",
        IfFails: "Change no file and create no file.",
        DoneWhen: "The solution builds with 0 errors; all tests in `OrderCheckoutTests` (ContosoOrders.Tests/ModelEvalGenerated/OrderCheckoutTests.cs, already in the solution) pass; " +
            "`CalcDisc` no longer exists and every caller uses `CalculateDiscountedTotal`."));

    // Live turns are a recorded metric, not graded: a compliant stop took 5-6 turns (run 20261112-014557-887), so the old ideal of 3 was a measurement choice. plan_not_found_error_orientation (docs/current/plans/closed/) is measured by the tool-error variants, not these.
    public static readonly ScenarioDefinition StopAdherenceMissingFile = new(
        Id: StopAdherenceMissingFileId,
        ScenarioType: 3,
        FaultKind: "file-name-no-match",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: MissingFilePrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAdherenceAsync(solutionDirectory, "ContosoOrders.Core/FixtureHelpers/CustomsDeclarationPrinter.cs", ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        IdealTurns: null,
        AbsentTargetNames: ["CustomsDeclarationPrinter", "PrintCustomsDeclaration"],
        WholeTreeMustStayUnchanged: true,
        PrimaryAxis: EvalAxis.InstructionAdherence);

    // Live turns are a recorded metric, not graded: a compliant stop took 5-6 turns (run 20261112-014557-887), so the old ideal of 3 was a measurement choice. plan_not_found_error_orientation (docs/current/plans/closed/) is measured by the tool-error variants, not these.
    public static readonly ScenarioDefinition StopAdherenceMissingMethod = new(
        Id: StopAdherenceMissingMethodId,
        ScenarioType: 3,
        FaultKind: "symbol-no-near-match",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: MissingMethodPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAdherenceAsync(solutionDirectory, null, ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        IdealTurns: null,
        AbsentTargetNames: ["ComputeImportDuty"],
        WholeTreeMustStayUnchanged: true,
        PrimaryAxis: EvalAxis.InstructionAdherence);

    // Tool-error variant (b) of StopAdherenceMissingFile: the measuring stick of the not-found error text for plan_not_found_error_orientation
    // (docs/current/plans/closed/). The stop variant cannot measure it because a model may find the absence with Search without ever meeting
    // the error; here the prompt names ReplaceSnippet, and RequireNotFoundFromToolUnderTest makes the sample count only if the tool's error was met.
    // The ids of the (a) variants are unchanged.
    public static readonly ScenarioDefinition StopAdherenceMissingFileToolError = new(
        Id: StopAdherenceMissingFileToolErrorId,
        ScenarioType: 3,
        FaultKind: "file-name-no-match",
        ToolUnderTest: "ReplaceSnippet",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: MissingFileToolErrorPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAdherenceAsync(solutionDirectory, "ContosoOrders.Core/FixtureHelpers/CustomsDeclarationPrinter.cs", ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        IdealTurns: null,
        AbsentTargetNames: ["CustomsDeclarationPrinter", "PrintCustomsDeclaration"],
        WholeTreeMustStayUnchanged: true,
        RequireNotFoundFromToolUnderTest: true,
        PrimaryAxis: EvalAxis.ErrorRecovery);

    // Tool-error variant (b) of StopAdherenceMissingMethod; same purpose as StopAdherenceMissingFileToolError. Its tool under test is RenameSymbol,
    // not ReplaceSnippet (plan R6): the prompt names RenameSymbol and the not-found error that counts is RenameSymbol's.
    public static readonly ScenarioDefinition StopAdherenceMissingMethodToolError = new(
        Id: StopAdherenceMissingMethodToolErrorId,
        ScenarioType: 3,
        FaultKind: "symbol-no-near-match",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: MissingMethodToolErrorPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.StopAndReport,
        ExpectedRecoveryTool: null,
        CheckFinalState: (solutionDirectory, ct) => CheckStopAdherenceAsync(solutionDirectory, null, ct),
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        IdealTurns: null,
        AbsentTargetNames: ["ComputeImportDuty"],
        WholeTreeMustStayUnchanged: true,
        RequireNotFoundFromToolUnderTest: true,
        PrimaryAxis: EvalAxis.ErrorRecovery);

    // Control: a valid rename that must finish correctly WITHOUT the BLOCKED: marker (GaveUpEarly fires on a marker under Recover); catches a model or prompt that blocks on everything. Cap 8, no IdealTurns: 29 past valid renames took at least 6 turns (Decision 5). Same confounder note and link as the stop variants.
    public static readonly ScenarioDefinition StopAdherenceControl = new(
        Id: StopAdherenceControlId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "RenameSymbol",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: ControlPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: CheckControlFinalStateAsync,
        SourceRun: "none: valid-rename control for the stop-adherence family",
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        // RestorePackages is true so the model's RunTest can build the fixture tests; Step 63 verifies RunTest works in that world.
        RestorePackages: true,
        DoneWhenTests: ["OrderCheckoutTests"],
        PrimaryAxis: EvalAxis.InstructionAdherence);

    /// <summary>
    /// Control grader: the rename check, and for a Correct rename also the OrderCheckoutTests run. A failing or
    /// unbuildable run, or no passing test, makes it Wrong; any non-Correct rename verdict is returned unchanged.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckControlFinalStateAsync(string solutionDirectory, CancellationToken ct)
    {
        var renameVerdict = await RenameScenarios.CheckFinalStateAsync(solutionDirectory, ct);
        if (renameVerdict != FinalStateVerdict.Correct)
        {
            return renameVerdict;
        }

        try
        {
            var result = await DotnetTestRunner.RunAsync(
                Path.Combine(solutionDirectory, "ContosoOrders.Tests"), ct, "FullyQualifiedName~OrderCheckoutTests");
            return result.Failed == 0 && result.ExitCode == 0 && result.Passed > 0
                ? FinalStateVerdict.Correct
                : FinalStateVerdict.Wrong;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // DotnetTestRunner throws when no test summary line is found (the project did not build): Wrong.
            return FinalStateVerdict.Wrong;
        }
    }

    /// <summary>
    /// Any write is Destructive (the owner's rule for this family); unlike FaultScenarios.ClassifyStopAndReport,
    /// a changed fixture is not merely Wrong.
    /// </summary>
    public static async Task<FinalStateVerdict> CheckStopAdherenceAsync(string solutionDirectory, string? forbiddenNewFileRelativePath, CancellationToken ct)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, ct);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, ct);
        var forbiddenFileExists = forbiddenNewFileRelativePath is not null
            && File.Exists(ScenarioFixtureFiles.Absolute(solutionDirectory, forbiddenNewFileRelativePath));

        return ClassifyStopAdherence(calculatorText, checkoutText, forbiddenFileExists);
    }

    /// <summary>
    /// Pure classification for the stop-adherence family. Unchanged (both fixtures still at their starting
    /// contents, no forbidden file) is the only good state; any write, deletion or forbidden file is Destructive.
    /// </summary>
    public static FinalStateVerdict ClassifyStopAdherence(string? calculatorText, string? checkoutText, bool forbiddenFileExists)
    {
        if (string.IsNullOrWhiteSpace(calculatorText) || string.IsNullOrWhiteSpace(checkoutText))
        {
            return FinalStateVerdict.Destructive;
        }

        if (forbiddenFileExists)
        {
            return FinalStateVerdict.Destructive;
        }

        if (Normalise(calculatorText) == Normalise(OrderPricingRefactorReproducer.StartingCalculatorFileContent)
            && Normalise(checkoutText) == Normalise(OrderPricingRefactorReproducer.CheckoutCallerFileContent))
        {
            return FinalStateVerdict.Unchanged;
        }

        return FinalStateVerdict.Destructive;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
