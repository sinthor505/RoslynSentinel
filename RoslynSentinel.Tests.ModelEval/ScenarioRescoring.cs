using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Re-scores a saved scenario run folder with the current ScenarioScorer rules. No model is called:
/// each sample's transcript.json and final-state folder are all the scorer needs.
/// </summary>
public static class ScenarioRescoring
{
    // Folder that helper files are staged into when a snapshot file name has no staged key.
    private const string FixtureHelpersPrefix = "ContosoOrders.Core/FixtureHelpers/";

    // Same options as SavedTranscriptScoring: AgentTranscript's list properties are get-only, so Populate fills them.
    private static readonly JsonSerializerOptions PopulateOptions = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    private static readonly string[] RequiredColumns =
    [
        "scenarioId", "scenarioType", "faultKind", "sampleIndex", "commit", "dirty", "liftRenames",
        "stopReason", "schemaDrift", "resultDrift", "inconclusiveReason",
    ];

    /// <summary>
    /// Returns the first staged key whose file name matches snapshotFileName (ordinal, case-insensitive), so a helper file
    /// that a rename moved is scored where it was staged. Otherwise returns the FixtureHelpers path.
    /// </summary>
    public static string TargetPath(IEnumerable<string> stagedKeys, string snapshotFileName)
    {
        foreach (var key in stagedKeys)
        {
            if (string.Equals(Path.GetFileName(key), snapshotFileName, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return FixtureHelpersPrefix + snapshotFileName;
    }

    /// <summary>
    /// Re-scores every row of &lt;runFolder&gt;/results.csv, appends each resulting sample to outputCsvPath and returns them in file order.
    /// Inconclusive rows stay Inconclusive and are not scored. Columns are read by header name, so the 28- and 30-column layouts both work.
    /// </summary>
    public static async Task<IReadOnlyList<ScenarioSample>> RescoreRunFolderAsync(
        string runFolder,
        Func<string, ScenarioDefinition?> findScenario,
        Func<ScenarioDefinition, string, CancellationToken, Task<FinalStateVerdict?>> verdictOf,
        string outputCsvPath,
        CancellationToken cancellationToken)
    {
        var resultsPath = Path.Combine(runFolder, "results.csv");
        var lines = (await File.ReadAllLinesAsync(resultsPath, Encoding.UTF8, cancellationToken))
            .Where(line => line.Trim().Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidOperationException($"'{resultsPath}' has no header line.");
        }

        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = ScenarioSampleCsv.SplitFields(lines[0]);
        for (var i = 0; i < header.Count; i++)
        {
            columns[header[i]] = i;
        }

        foreach (var name in RequiredColumns)
        {
            if (!columns.ContainsKey(name))
            {
                throw new FormatException($"'{resultsPath}' has no \"{name}\" column.");
            }
        }

        var samples = new List<ScenarioSample>();
        for (var line = 1; line < lines.Count; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = ScenarioSampleCsv.SplitFields(lines[line]);
            var scenarioId = Field(columns, fields, "scenarioId");
            var scenarioType = int.Parse(Field(columns, fields, "scenarioType"), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var faultKind = Field(columns, fields, "faultKind");
            var sampleIndex = int.Parse(Field(columns, fields, "sampleIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var commit = Field(columns, fields, "commit");
            var dirty = bool.TryParse(Field(columns, fields, "dirty"), out var dirtyValue) ? dirtyValue : (bool?)null;
            var liftRenames = Field(columns, fields, "liftRenames");
            var schemaDrift = bool.Parse(Field(columns, fields, "schemaDrift"));
            var resultDrift = int.Parse(Field(columns, fields, "resultDrift"), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var inconclusiveReason = Field(columns, fields, "inconclusiveReason");

            var sampleFolder = Path.Combine(runFolder, "sample-" + sampleIndex.ToString("D2", CultureInfo.InvariantCulture));
            var transcriptPath = Path.Combine(sampleFolder, "transcript.json");

            ScenarioSample sample;
            if (inconclusiveReason.Length > 0)
            {
                sample = new ScenarioSample(
                    scenarioId, scenarioType, faultKind, sampleIndex, commit, dirty, liftRenames,
                    null, schemaDrift, resultDrift, inconclusiveReason, transcriptPath);
            }
            else
            {
                var scenario = findScenario(scenarioId)
                    ?? throw new InvalidOperationException($"Scenario '{scenarioId}' in '{resultsPath}' is not defined.");
                var json = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
                var transcript = JsonSerializer.Deserialize<AgentTranscript>(json, PopulateOptions)
                    ?? throw new InvalidOperationException($"Transcript '{transcriptPath}' deserialized to null.");
                var finalStateFolder = Path.Combine(sampleFolder, "final-state");
                var verdict = Directory.Exists(finalStateFolder)
                    ? await verdictOf(scenario, finalStateFolder, cancellationToken)
                    : null;
                var stopReason = ParseStopReason(Field(columns, fields, "stopReason"));
                var score = ScenarioScorer.Score(
                    transcript,
                    scenario.ToolUnderTest,
                    scenario.Expected,
                    scenario.ExpectedRecoveryTool,
                    verdict,
                    stopReason);
                sample = new ScenarioSample(
                    scenarioId, scenarioType, faultKind, sampleIndex, commit, dirty, liftRenames,
                    score, schemaDrift, resultDrift, inconclusiveReason, transcriptPath);
            }

            ScenarioSampleCsv.Append(outputCsvPath, sample);
            samples.Add(sample);
        }

        return samples;
    }

    private static string Field(IReadOnlyDictionary<string, int> columns, List<string> fields, string name) =>
        columns.TryGetValue(name, out var index) && index < fields.Count ? fields[index] : string.Empty;

    private static AgentStopReason? ParseStopReason(string value) =>
        Enum.TryParse<AgentStopReason>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
