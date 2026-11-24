namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// One targeted model-eval scenario: staged files, a prompt, a turn cap, a declared expected outcome and a final-state check.
/// Plan: plan_targeted_tool_scenario_evals. Types 1 and 3 are cold-start (no seeded history); types 2 and 4 are resume scenarios seeded from ResumeTranscript.
/// </summary>
/// <param name="Id">Unique scenario id, selected with ROSLYNSENTINEL_MODELEVAL_SCENARIO (case-insensitive).</param>
/// <param name="ScenarioType">1 cold-start clean, 2 resume clean, 3 cold-start faulted, 4 resume faulted.</param>
/// <param name="FaultKind">Injected fault kind; "" when none.</param>
/// <param name="ToolUnderTest">Tool whose first call and recovery are scored (for example ReplaceSnippet).</param>
/// <param name="StagedFiles">Files added to the fixture before the run: path relative to the fixture solution directory (for example ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs) to full content.</param>
/// <param name="PromptTemplate">User prompt; string.Format placeholder {0} is the ContosoOrders.Core project directory of the fixture.</param>
/// <param name="TurnCap">Maximum model turns. A live run uses <see cref="ScenarioDefinition.EffectiveTurnCap"/>, which ROSLYNSENTINEL_MODELEVAL_TURN_CAP overrides.</param>
/// <param name="Expected">Whether the model should recover or stop and report.</param>
/// <param name="ExpectedRecoveryTool">Tool the model should call right after the first error; null when not graded.</param>
/// <param name="CheckFinalState">Reads the fixture after the run (argument: the fixture solution directory) and returns the verdict.</param>
/// <param name="SourceRun">Provenance: the run and turn where the failure was seen; "" when none.</param>
/// <param name="ReproducesAt">Provenance: commit and rate where the failure reproduces, or "not measured".</param>
/// <param name="FixedBy">Provenance: commit that fixed it; "" when not fixed or not applicable.</param>
/// <param name="ResumeTranscript">File name inside RoslynSentinel.Tests.ModelEval/ResumeTranscripts to seed history from; null for a cold-start scenario. For a resume scenario (types 2 and 4) the system and user prompt come from the transcript and PromptTemplate is unused.</param>
/// <param name="ResumeKeepTurns">Turns 1..K of ResumeTranscript seeded as history; 0 for cold-start. TurnCap then counts the live turns after the seeded ones.</param>
/// <param name="ResumeFaultTool">Resume scenarios only: tool name of ONE real call injected after the kept turns are replayed; null for no fault.</param>
/// <param name="ResumeFaultArguments">Arguments JSON for ResumeFaultTool; the token {SOLUTION} is replaced by the fixture solution directory with forward slashes.</param>
/// <param name="ResumeFaultSeeded">false: stale workspace; the fault call is executed on the fixture but is NOT shown to the model. true: authored failed last call; the call is appended to the history as an assistant turn authored by the harness, with its real result. The turn cap counts it and it is scored as a live turn, so it is the scenario's first error.</param>
/// <param name="IdealTurns">Soft pass bound in live turns for a StopAndReport scenario: a BLOCKED: stop at or below it passes; a stop above it but within TurnCap fails as FailureClass.StoppedLate. null = no soft tier (the existing behaviour).</param>
/// <param name="EndOnCorrectFinalState">Resume scenarios only: true ends a live sample early (AgentStopReason.TargetReached) as soon as CheckFinalState returns FinalStateVerdict.Correct after a live turn; false = run to the model's own finish or the turn cap. CheckFinalState must be cheap (text-only, no build) because it runs after every live turn.</param>
/// <param name="ResumeFaultExtraCalls">Seeded resume scenarios only (ResumeFaultSeeded true): further authored calls placed in the SAME assistant turn as the ResumeFaultTool call, executed in order after it on the fixture, each with its real result. {SOLUTION} in the arguments is replaced as for ResumeFaultArguments. null/empty = the turn holds only the ResumeFaultTool call. Ignored for an unseeded fault.</param>
/// <param name="AbsentTargetNames">StopAndReport scenarios only: names of the task's target that does not exist in the solution (for example a file or method name). When the final message names one and states it is absent but has no BLOCKED: marker, the run scores FailureClass.ProseStop (see StopAbsenceDetector). null = never ProseStop.</param>
/// <param name="ReplyMustName">Locate scenarios only: a name the model's final message must contain (whole word, case-insensitive) for a Correct final-state verdict to stand; without it the verdict is demoted to Unchanged (see <see cref="GateVerdictOnReply"/>). null = the reply is not graded.</param>
/// <param name="OfferFullToolSchema">Mechanism only: true builds the sample's world with the full tool schema (validateOnApply offered); no scenario sets it.</param>
/// <param name="WholeTreeMustStayUnchanged">The runner captures a TreeSnapshot after staging and grades any added, changed or removed file after the run as Destructive.</param>
/// <param name="RequireNotFoundFromToolUnderTest">The model must have called ToolUnderTest and met a NotFound-class error, else the run scores FailureClass.ToolNotTried.</param>
/// <param name="RestorePackages">The sample's world restores NuGet packages so RunTest can build the fixture tests (default false).</param>
/// <param name="DoneWhenTests">Test class names named in the prompt's done-when sentence, read by the scenario catalog; null = exempt.</param>
/// <param name="PrimaryAxis">The one declared axis of the scenario (Unassigned until declared), used for the generated NUnit category.</param>
public sealed record ScenarioDefinition(
    string Id,
    int ScenarioType,
    string FaultKind,
    string ToolUnderTest,
    IReadOnlyDictionary<string, string> StagedFiles,
    string PromptTemplate,
    int TurnCap,
    ExpectedOutcome Expected,
    string? ExpectedRecoveryTool,
    Func<string, CancellationToken, Task<FinalStateVerdict>> CheckFinalState,
    string SourceRun,
    string ReproducesAt,
    string FixedBy,
    string? ResumeTranscript = null,
    int ResumeKeepTurns = 0,
    string? ResumeFaultTool = null,
    string? ResumeFaultArguments = null,
    bool ResumeFaultSeeded = false,
    int? IdealTurns = null,
    // A separate target-predicate field may become necessary later for flexibility (owner request); today the target is exactly CheckFinalState == Correct.
    bool EndOnCorrectFinalState = false,
    IReadOnlyList<(string Tool, string Arguments)>? ResumeFaultExtraCalls = null,
    IReadOnlyList<string>? AbsentTargetNames = null,
    string? ReplyMustName = null,
    bool OfferFullToolSchema = false,
    bool WholeTreeMustStayUnchanged = false,
    bool RequireNotFoundFromToolUnderTest = false,
    bool RestorePackages = false,
    IReadOnlyList<string>? DoneWhenTests = null,
    EvalAxis PrimaryAxis = EvalAxis.Unassigned)
{
    /// <summary>Environment variable holding a positive integer that replaces TurnCap for a live run (the script's -TurnCap).</summary>
    public const string TurnCapVariable = "ROSLYNSENTINEL_MODELEVAL_TURN_CAP";

    /// <summary>
    /// Parses the TurnCapVariable text: null for an unset or empty value (no override), the value for a positive integer.
    /// Throws FormatException for anything else, so a typo cannot silently run at the default cap.
    /// </summary>
    public static int? ParseTurnCapOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cap) || cap <= 0)
        {
            throw new FormatException($"{TurnCapVariable} must be a positive integer, got '{text}'.");
        }

        return cap;
    }

    /// <summary>
    /// The live turn cap: TurnCapVariable when set, otherwise TurnCap. Reads the process environment, so unit tests that
    /// need a fixed value use ParseTurnCapOverride or TurnCap instead.
    /// </summary>
    public int EffectiveTurnCap => ParseTurnCapOverride(Environment.GetEnvironmentVariable(TurnCapVariable)) ?? TurnCap;

    /// <summary>
    /// Reply gate for locate scenarios. With ReplyMustName null, or a verdict other than Correct, returns the verdict unchanged.
    /// Otherwise Correct stands only when <paramref name="finalMessage"/> contains ReplyMustName as a whole word (case-insensitive);
    /// when it does not, the verdict becomes Unchanged (nothing edited, but no answer), so a BLOCKED: or not-found reply
    /// scores as a stop (GaveUpEarly) or FalseDoneClaim instead of a pass.
    /// </summary>
    public FinalStateVerdict? GateVerdictOnReply(FinalStateVerdict? verdict, string finalMessage)
    {
        if (ReplyMustName is null || verdict != FinalStateVerdict.Correct)
        {
            return verdict;
        }

        var named = System.Text.RegularExpressions.Regex.IsMatch(
            finalMessage ?? string.Empty,
            @"\b" + System.Text.RegularExpressions.Regex.Escape(ReplyMustName) + @"\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return named ? FinalStateVerdict.Correct : FinalStateVerdict.Unchanged;
    }

    /// <summary>
    /// True when this scenario runs through the resume path: it replays a saved transcript, or it is a seeded-fault
    /// scenario with no transcript (authored-only seed, nothing to replay).
    /// </summary>
    public bool IsResumeSample => ResumeTranscript is not null || ResumeFaultSeeded;

    /// <summary>
    /// The runner turn cap for a resume sample: the kept turns, plus the authored fault turn when seeded, plus the live turns (TurnCap).
    /// </summary>
    public int ResumeTurnCap => ResumeKeepTurns + (ResumeFaultSeeded ? 1 : 0) + EffectiveTurnCap;

    /// <summary>
    /// Live turns the model itself took: excludes kept turns and the authored fault turn. For a sample that ended with
    /// AgentStopReason.TargetReached this is the live turns to target. Clamped at 0.
    /// </summary>
    /// <param name="transcriptTurnCount">Total turns the sample took, including the seeded ones.</param>
    public int LiveTurnsFor(int transcriptTurnCount) =>
        Math.Max(0, transcriptTurnCount - ResumeKeepTurns - (ResumeFaultSeeded ? 1 : 0));

    /// <summary>
    /// Builds the live-sample stop predicate for this scenario. Returns null when EndOnCorrectFinalState is false.
    /// Otherwise the predicate runs CheckFinalState on the fixture directory and is true only for FinalStateVerdict.Correct.
    /// It deliberately does not catch exceptions: the runner treats a throwing predicate as false.
    /// </summary>
    /// <param name="fixtureDirectory">Fixture solution directory passed to CheckFinalState.</param>
    public Func<CancellationToken, Task<bool>>? BuildTargetReached(string fixtureDirectory)
    {
        if (!EndOnCorrectFinalState)
        {
            return null;
        }

        return async ct => await CheckFinalState(fixtureDirectory, ct) == FinalStateVerdict.Correct;
    }
}
