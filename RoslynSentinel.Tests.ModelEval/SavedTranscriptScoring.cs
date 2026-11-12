using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Scores a saved transcript.json outside any scenario run and appends one results CSV line for it.
/// No model is called: the transcript already holds every turn and tool call.
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

        var score = ScenarioScorer.Score(
            transcript,
            toolUnderTest,
            ExpectedOutcome.Recover,
            expectedRecoveryTool: null,
            verdict: null,
            stopReason: transcript.StopReason);

        var runFolderLeaf = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
        // ScenarioType 0 means a saved transcript scored outside a scenario (scenario types are 1-4).
        var sample = new ScenarioSample(
            ScenarioId: "saved:" + runFolderLeaf,
            ScenarioType: 0,
            FaultKind: "",
            SampleIndex: 0,
            Commit: transcript.CommitHash ?? "",
            Dirty: transcript.WorkingTreeDirty,
            LiftRenames: "unset",
            Score: score,
            SchemaDrift: false,
            ResultDrift: 0,
            InconclusiveReason: "",
            TranscriptPath: path,
            ErrorPolicyMode: transcript.ErrorPolicyMode ?? "",
            ErrorPolicyHash: transcript.ErrorPolicyHash ?? "");

        ScenarioSampleCsv.Append(csvPath, sample);
        return sample;
    }
}
