using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Locate family: cold-start scenarios on the same staged files as FaultScenarios.RenameSymbolNoSuchMethod, but the task
/// is a lookup, not an edit. The model must reply with the real method's name (CalcDisc) and change no file. They are
/// graded on the final reply: CheckFinalState reports Correct while both fixture files are untouched, and
/// <see cref="ScenarioDefinition.GateVerdictOnReply"/> demotes it to Unchanged when the reply does not name the method, so a
/// BLOCKED: or not-found reply scores as GaveUpEarly (a stop) and a reply without the name as FalseDoneClaim; any edit is
/// Destructive. Scenarios are not registered here; ScenarioRegistry owns that.
/// </summary>
public static class LocateScenarios
{
    public const string LocateMethodByGuessedNameId = "LocateMethodByGuessedName";
    public const string LocateMethodByDescriptionId = "LocateMethodByDescription";

    /// <summary>The real method both scenarios expect the reply to name.</summary>
    public const string RealMethodName = "CalcDisc";

    private const string CommonSourceRun = "none: constructed locate variant of FaultScenarios.RenameSymbolNoSuchMethod (a BLOCKED: stop is accepted there, owner 2026-11-13); not seen in a recorded run";
    private const string CommonReproducesAt = "measured by this scenario (no fault to reproduce; the reply naming the real method is what is under test)";

    public const string LocateMethodByGuessedNamePrompt = """
        # Task: Locate a method in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        ## Background

        The user is looking for a method they think is called `ApplyLoyaltyBonus` in
        `{0}/FixtureHelpers/OrderPricingCalculator.cs`.

        ## Steps (apply the step below)

        1. Locate the `ApplyLoyaltyBonus` method or a likely candidate and report back to the user with its name.
        """;

    public const string LocateMethodByDescriptionPrompt = """
        # Task: Locate a method in FixtureHelpers/OrderPricingCalculator.cs

        The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below.

        ## Background

        `{0}/FixtureHelpers/OrderPricingCalculator.cs` has a method that computes an order's
        discounted total. It is called from one other file, `{0}/FixtureHelpers/OrderCheckout.cs`.

        ## Steps (apply the step below)

        1. Locate that method and reply with its name.
        """;

    public static readonly ScenarioDefinition LocateMethodByGuessedName = new(
        Id: LocateMethodByGuessedNameId,
        ScenarioType: 3,
        FaultKind: "symbol-misnamed-locate",
        ToolUnderTest: "Search",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: LocateMethodByGuessedNamePrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: CheckNoEditAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        ReplyMustName: RealMethodName);

    public static readonly ScenarioDefinition LocateMethodByDescription = new(
        Id: LocateMethodByDescriptionId,
        ScenarioType: 1,
        FaultKind: "",
        ToolUnderTest: "Search",
        StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
        PromptTemplate: LocateMethodByDescriptionPrompt,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: CheckNoEditAsync,
        SourceRun: CommonSourceRun,
        ReproducesAt: CommonReproducesAt,
        FixedBy: "",
        ReplyMustName: RealMethodName);

    /// <summary>Reads the two fixture files after a locate run and returns Correct when both are untouched, else Destructive.</summary>
    public static async Task<FinalStateVerdict> CheckNoEditAsync(string solutionDirectory, CancellationToken ct)
    {
        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, ct);
        var checkoutText = await ScenarioFixtureFiles.ReadFileOrNullAsync(solutionDirectory, ScenarioFixtureFiles.CheckoutRelativePath, ct);
        return ClassifyNoEdit(calculatorText, checkoutText);
    }

    /// <summary>
    /// Pure classification: both files at their starting contents is Correct (nothing edited; the reply gate decides the rest),
    /// any write or deletion is Destructive.
    /// </summary>
    public static FinalStateVerdict ClassifyNoEdit(string? calculatorText, string? checkoutText) =>
        StopAdherenceScenarios.ClassifyStopAdherence(calculatorText, checkoutText, forbiddenFileExists: false) == FinalStateVerdict.Unchanged
            ? FinalStateVerdict.Correct
            : FinalStateVerdict.Destructive;
}
