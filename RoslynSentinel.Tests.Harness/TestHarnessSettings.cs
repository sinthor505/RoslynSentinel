using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

[StartupSettingGroup("Test harness")]
public static class TestHarnessSettings
{
    public static readonly StartupSetting<string?> ModelEvalContractSetting = StartupSetting.String(null, EvalContractOptions.ContractEnvVar, null, "Name of the eval workflow contract variant (a variant name, not a path); unset selects the default variant.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalContractEndingsSetting = StartupSetting.String(null, EvalContractOptions.EndingsEnvVar, null, "Ending variant names for the eval contract.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalTurnCapSetting = StartupSetting.String(null, ScenarioDefinition.TurnCapVariable, null, "Maximum number of turns per scenario.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalRescoreRunSetting = StartupSetting.String(null, ScenarioRescoreRunTests.RunVariable, null, "Run identifier for the rescore test.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalScoreTranscriptSetting = StartupSetting.String(null, SavedTranscriptScoring.TranscriptVariable, null, "Path of the saved transcript to score.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalScoreCsvSetting = StartupSetting.String(null, SavedTranscriptScoring.CsvVariable, null, "Path of the CSV output for transcript scoring.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalScoreToolSetting = StartupSetting.String(null, SavedTranscriptScoring.ToolVariable, null, "Tool name used when scoring a saved transcript.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeTranscriptSetting = StartupSetting.String(null, ResumeSettings.TranscriptVariable, null, "Path of the transcript to resume from.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeKeepTurnsSetting = StartupSetting.String(null, ResumeSettings.KeepTurnsVariable, null, "Number of turns to keep when resuming a transcript.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeSamplesSetting = StartupSetting.String(null, ResumeSettings.SamplesVariable, null, "Number of samples to take when resuming a transcript.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeContinueTurnsSetting = StartupSetting.String(null, ResumeSettings.ContinueTurnsVariable, null, "Number of turns to continue when resuming a transcript.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdateToolSurfaceGoldenSetting = StartupSetting.String(null, ToolSurfaceGolden.UpdateEnvironmentVariable, null, "Rewrite the tool surface golden file when set.", SettingScope.TestHarness, SettingKind.Live);

    public static readonly StartupSetting<string?> ModelEvalReplayTranscriptSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT", null, "Path of the transcript to replay in the replay test.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalReplaySizeSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE", null, "Size of the replayed model run.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalToolsetSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_TOOLSET", null, "Toolset used by the model eval run.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalEditArmSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_EDIT_ARM", null, "Edit arm used by the model eval run.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalStepWallClockMinutesSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES", null, "Wall clock limit in minutes for each eval step.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalScenarioSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_SCENARIO", null, "Name of the scenario to run in the model eval.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalScenarioSamplesSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES", null, "Number of samples to take per scenario.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalPromptVariantSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT", null, "Prompt variant used by the model eval run.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalSizesSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_SIZES", null, "Sizes to run in the model eval.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalRepeatsSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_REPEATS", null, "Number of repeats per model eval case.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalReplayEarlyStopSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_REPLAY_EARLY_STOP", null, "End the replay test early when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeLiteralCaseSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE", null, "Literal case selection for the resume test.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> ModelEvalResumeStepSetting = StartupSetting.String(null, "ROSLYNSENTINEL_MODELEVAL_RESUME_STEP", null, "Step selection for the resume test.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdateGeneratedDocsSetting = StartupSetting.String(null, "ROSLYNSENTINEL_UPDATE_GENERATED_DOCS", null, "Rewrite the generated docs when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdateLongMessageBaselineSetting = StartupSetting.String(null, "ROSLYNSENTINEL_UPDATE_LONG_MESSAGE_BASELINE", null, "Rewrite the long message baseline when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdatePlacementBaselineSetting = StartupSetting.String(null, "ROSLYNSENTINEL_UPDATE_PLACEMENT_BASELINE", null, "Rewrite the placement baseline when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdateHintBaselineSetting = StartupSetting.String(null, "ROSLYNSENTINEL_UPDATE_HINT_BASELINE", null, "Rewrite the hint baseline when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> UpdateToolReturnBaselineSetting = StartupSetting.String(null, "ROSLYNSENTINEL_UPDATE_TOOL_RETURN_BASELINE", null, "Rewrite the tool return baseline when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> TestKeepSetting = StartupSetting.String(null, "ROSLYNSENTINEL_TEST_KEEP", null, "Keep test output files when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> TestEnvPassthroughSetting = StartupSetting.String(null, "ROSLYNSENTINEL_TEST_ENV_PASSTHROUGH", null, "Environment variable names passed through to the test server.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> TestServerOutputSetting = StartupSetting.String(null, "ROSLYNSENTINEL_TEST_SERVER_OUTPUT", null, "Show test server output when set.", SettingScope.TestHarness, SettingKind.Live);
    public static readonly StartupSetting<string?> FakeWriteAuditSetting = StartupSetting.String(null, "ROSLYNSENTINEL_FAKE_WRITE_AUDIT", null, "Audit fake file writes when set.", SettingScope.TestHarness, SettingKind.Live);
}
