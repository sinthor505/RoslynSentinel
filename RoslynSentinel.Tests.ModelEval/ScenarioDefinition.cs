namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// One targeted model-eval scenario: staged files, a prompt, a turn cap, a declared expected outcome and a final-state check.
/// Plan: plan_targeted_tool_scenario_evals. Resume scenarios (types 2 and 4) are added by a later step; types 1 and 3 need no seeded history.
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
    string FixedBy);
