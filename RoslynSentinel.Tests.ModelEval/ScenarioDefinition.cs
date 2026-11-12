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
/// <param name="TurnCap">Maximum model turns.</param>
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
/// <param name="ResumeFaultSeeded">false: the fault call runs on the fixture but is NOT shown to the model (stale workspace). true: a failed last call shown to the model; not implemented yet, the harness returns Inconclusive.</param>
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
    bool ResumeFaultSeeded = false);
