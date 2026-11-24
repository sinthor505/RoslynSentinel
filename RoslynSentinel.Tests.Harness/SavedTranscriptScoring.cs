using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Scores a saved transcript.json outside any scenario run and appends one results CSV line for it.
/// No model is called: the transcript already holds every turn and tool call.
/// The scenario is resolved from the transcript's ScenarioId, else the run folder's results.csv row for its sample-NN folder, and scored
/// with the scenario's own settings (including the tool-not-tried requirement); with neither the sample is scored under a synthetic "saved:" id.
/// </summary>
public static class SavedTranscriptScoring
{
    /// <summary>Environment variable holding the transcript.json path or the run folder that holds it.</summary>
    public const string TranscriptVariable = "ROSLYNSENTINEL_MODELEVAL_SCORE_TRANSCRIPT";

    /// <summary>Environment variable holding the results CSV to append to; unset means score.csv beside the transcript.</summary>
    public const string CsvVariable = "ROSLYNSENTINEL_MODELEVAL_SCORE_CSV";

    /// <summary>Environment variable holding the tool whose first call and recovery are scored (default ReplaceSnippet).</summary>
    public const string ToolVariable = "ROSLYNSENTINEL_MODELEVAL_SCORE_TOOL";

    /// <summary>
    /// AgentTranscript's list properties (Turns, Tools) are get-only, so the default deserializer skips them and the turns are lost.
    /// Populate fills the existing lists instead; TranscriptResume.ResumeSource exists for the same reason.
    /// </summary>
    private static readonly JsonSerializerOptions PopulateOptions = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    /// <summary>
    /// Scores the transcript at <paramref name="transcriptPathOrFolder"/> and appends one line to <paramref name="csvPath"/>.
    /// A saved transcript records no verdict; its stop reason is scored when the transcript carries one (older transcripts do not), and the call-level figures are filled in.
    /// When the scenario resolves (see <see cref="ResolveScenario"/>) its own tool, expectation, caps and tool-not-tried requirement are used and
    /// <paramref name="toolUnderTest"/> is ignored; otherwise the sample is scored as today with <paramref name="toolUnderTest"/>.
    /// </summary>
    public static ScenarioSample ScoreFile(string transcriptPathOrFolder, string toolUnderTest, string csvPath)
    {
        var path = TranscriptResume.ResolveTranscriptPath(transcriptPathOrFolder);
        var json = File.ReadAllText(path);
        var transcript = JsonSerializer.Deserialize<AgentTranscript>(json, PopulateOptions)
            ?? throw new InvalidOperationException($"Transcript '{path}' deserialized to null.");
        if (json.Contains("\"ToolCalls\"") && transcript.Turns.Count == 0)
        {
            throw new InvalidOperationException($"Transcript '{path}' has turns in the file but none were read.");
        }

        var scenario = ResolveScenario(path, transcript);
        ScoreResult score;
        if (scenario is not null)
        {
            // Same arguments ScenarioRescoring uses; no final-state folder exists for a lone transcript, so the verdict stays null.
            score = ScenarioScorer.Score(
                transcript,
                scenario.ToolUnderTest,
                scenario.Expected,
                scenario.ExpectedRecoveryTool,
                verdict: null,
                stopReason: transcript.StopReason,
                seededTurns: scenario.ResumeKeepTurns,
                idealTurns: scenario.IdealTurns,
                turnCap: scenario.TurnCap,
                absentTargetNames: scenario.AbsentTargetNames,
                requireNotFoundFromToolUnderTest: scenario.RequireNotFoundFromToolUnderTest);
        }
        else
        {
            // Fallback: no scenario id resolved, so the caller's tool and the Recover expectation apply and the tool-not-tried requirement cannot be scored.
            score = ScenarioScorer.Score(
                transcript,
                toolUnderTest,
                ExpectedOutcome.Recover,
                expectedRecoveryTool: null,
                verdict: null,
                stopReason: transcript.StopReason,
                requireNotFoundFromToolUnderTest: false);
        }

        var runFolderLeaf = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
        // ScenarioType 0 means a saved transcript scored outside a scenario (scenario types are 1-4).
        var sample = new ScenarioSample(
            ScenarioId: scenario?.Id ?? "saved:" + runFolderLeaf,
            ScenarioType: scenario?.ScenarioType ?? 0,
            FaultKind: scenario?.FaultKind ?? "",
            SampleIndex: 0,
            Commit: transcript.CommitHash ?? "",
            Dirty: transcript.WorkingTreeDirty,
            LiftRenames: "unset",
            Score: score,
            SchemaDrift: false,
            ResultDrift: 0,
            InconclusiveReason: "",
            TranscriptPath: path,
            LiveTurnsToTarget: scenario?.LiveTurnsFor(score.TurnCount),
            ErrorPolicyMode: transcript.ErrorPolicyMode ?? "",
            ErrorPolicyHash: transcript.ErrorPolicyHash ?? "",
            ModelKey: transcript.ModelKey ?? "",
            Temperature: transcript.Temperature,
            TopP: transcript.TopP,
            ContractHash: transcript.ContractHash ?? "",
            ContractPlacement: transcript.ContractPlacement ?? "");

        ScenarioSampleCsv.Append(csvPath, sample);
        return sample;
    }

    /// <summary>
    /// The scenario a saved transcript belongs to: the id stamped in the transcript header, else the id of its row in the run folder's
    /// results.csv (transcript at &lt;runFolder&gt;/sample-NN/transcript.json, row with sampleIndex NN). Null when neither names a registered scenario;
    /// an id the registry does not know is treated as no id, never an error.
    /// </summary>
    private static ScenarioDefinition? ResolveScenario(string transcriptPath, AgentTranscript transcript)
    {
        var scenario = ScenarioRegistry.Find(transcript.ScenarioId);
        if (scenario is not null)
        {
            return scenario;
        }

        var sampleFolder = Path.GetDirectoryName(Path.GetFullPath(transcriptPath));
        var leaf = Path.GetFileName(sampleFolder);
        const string SamplePrefix = "sample-";
        if (sampleFolder is null
            || leaf is null
            || !leaf.StartsWith(SamplePrefix, StringComparison.Ordinal)
            || !int.TryParse(leaf.AsSpan(SamplePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sampleIndex))
        {
            return null;
        }

        var runFolder = Path.GetDirectoryName(sampleFolder);
        return runFolder is null
            ? null
            : ScenarioRegistry.Find(ScenarioSampleCsv.TryReadScenarioId(Path.Combine(runFolder, "results.csv"), sampleIndex));
    }
}
