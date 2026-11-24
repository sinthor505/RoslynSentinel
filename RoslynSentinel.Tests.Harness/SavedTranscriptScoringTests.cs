using System.Text.Json;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class SavedTranscriptScoringTests
{
    private const string Tool = "ReplaceSnippet";
    private const string NotFoundResultJson = "{\"toolCall\":{},\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}";

    [Test]
    public void ScoreFile_WritesHeaderAndOneLine_ForAHandBuiltTranscript()
    {
        var folder = Path.Combine(Path.GetTempPath(), "savedscore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var transcript = new AgentTranscript();
            transcript.Turns.Add(Turn(1, Call(Tool, "{\"a\":1}", isError: true, resultJson: NotFoundResultJson)));
            transcript.Turns.Add(Turn(2, Call(Tool, "{\"a\":2}")));
            File.WriteAllText(Path.Combine(folder, "transcript.json"), JsonSerializer.Serialize(transcript));
            var csvPath = Path.Combine(folder, "score.csv");

            var sample = SavedTranscriptScoring.ScoreFile(folder, Tool, csvPath);

            var lines = File.ReadAllLines(csvPath);
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That(sample.Score, Is.Not.Null);
            Assert.That(sample.Score!.FirstErrorCode, Is.EqualTo("NotFound"));
            Assert.That(sample.Score.CallsToRecover, Is.EqualTo(1));
            Assert.That(sample.ScenarioId, Does.StartWith("saved:"));
            Assert.That(ScenarioSampleCsv.Parse(lines[1]), Is.EqualTo(sample));
            Assert.That(sample.Score.StopReason, Is.Null);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ScoreFile_ScoresStopReason_WhenTheTranscriptRecordsOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "savedscore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var transcript = new AgentTranscript { StopReason = AgentStopReason.TurnCapExceeded };
            transcript.Turns.Add(Turn(1, Call(Tool, "{\"a\":1}", isError: true, resultJson: NotFoundResultJson)));
            transcript.Turns.Add(Turn(2, Call(Tool, "{\"a\":2}")));
            File.WriteAllText(Path.Combine(folder, "transcript.json"), JsonSerializer.Serialize(transcript));
            var csvPath = Path.Combine(folder, "score.csv");

            var sample = SavedTranscriptScoring.ScoreFile(folder, Tool, csvPath);

            var parsed = ScenarioSampleCsv.Parse(File.ReadAllLines(csvPath)[1]);
            Assert.That(sample.Score, Is.Not.Null);
            Assert.That(sample.Score!.StopReason, Is.EqualTo(AgentStopReason.TurnCapExceeded));
            Assert.That(sample.Score.FailureClass, Is.EqualTo(FailureClass.BudgetOut));
            Assert.That(parsed, Is.EqualTo(sample));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ScoreFile_UsesTheScenarioStampedInTheTranscript_AndScoresToolNotTried()
    {
        var folder = NewFolder();
        try
        {
            // The caller's tool is a different one on purpose: a resolved scenario brings its own ToolUnderTest (ReplaceSnippet).
            var transcript = NeverTriedTheToolTranscript(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId);
            File.WriteAllText(Path.Combine(folder, "transcript.json"), JsonSerializer.Serialize(transcript));

            var sample = SavedTranscriptScoring.ScoreFile(folder, "NotTheScenariosTool", Path.Combine(folder, "score.csv"));

            Assert.That(sample.ScenarioId, Is.EqualTo(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId));
            Assert.That(sample.ScenarioType, Is.EqualTo(StopAdherenceScenarios.StopAdherenceMissingFileToolError.ScenarioType));
            Assert.That(sample.Score, Is.Not.Null);
            Assert.That(sample.Score!.FailureClass, Is.EqualTo(FailureClass.ToolNotTried));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ScoreFile_ReadsTheScenarioIdFromResultsCsv_WhenTheTranscriptCarriesNone()
    {
        var runFolder = NewFolder();
        try
        {
            var sampleFolder = Path.Combine(runFolder, "sample-02");
            Directory.CreateDirectory(sampleFolder);
            // Minimal header: the lookup reads columns by name, so only scenarioId and sampleIndex are needed. Row 1 is another sample's.
            File.WriteAllText(
                Path.Combine(runFolder, "results.csv"),
                "scenarioId,sampleIndex\r\nNoSuchScenario,1\r\n" + StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId + ",2\r\n");
            var transcript = NeverTriedTheToolTranscript(scenarioId: null);
            File.WriteAllText(Path.Combine(sampleFolder, "transcript.json"), JsonSerializer.Serialize(transcript));

            var sample = SavedTranscriptScoring.ScoreFile(sampleFolder, Tool, Path.Combine(runFolder, "score.csv"));

            Assert.That(sample.ScenarioId, Is.EqualTo(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId));
            Assert.That(sample.Score!.FailureClass, Is.EqualTo(FailureClass.ToolNotTried));
        }
        finally
        {
            Directory.Delete(runFolder, recursive: true);
        }
    }

    [Test]
    public void ScoreFile_KeepsTheSyntheticId_WhenResultsCsvHasNoRowForTheSample()
    {
        var runFolder = NewFolder();
        try
        {
            var sampleFolder = Path.Combine(runFolder, "sample-03");
            Directory.CreateDirectory(sampleFolder);
            File.WriteAllText(
                Path.Combine(runFolder, "results.csv"),
                "scenarioId,sampleIndex\r\n" + StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId + ",1\r\n");
            var transcript = NeverTriedTheToolTranscript(scenarioId: null);
            File.WriteAllText(Path.Combine(sampleFolder, "transcript.json"), JsonSerializer.Serialize(transcript));

            var sample = SavedTranscriptScoring.ScoreFile(sampleFolder, Tool, Path.Combine(runFolder, "score.csv"));

            Assert.That(sample.ScenarioId, Is.EqualTo("saved:sample-03"));
            Assert.That(sample.Score!.FailureClass, Is.Not.EqualTo(FailureClass.ToolNotTried));
        }
        finally
        {
            Directory.Delete(runFolder, recursive: true);
        }
    }

    [Test]
    public void ScoreFile_FallsBackToTheSyntheticId_WhenTheTranscriptNamesAnUnknownScenario()
    {
        var folder = NewFolder();
        try
        {
            var transcript = NeverTriedTheToolTranscript("NoSuchScenario");
            File.WriteAllText(Path.Combine(folder, "transcript.json"), JsonSerializer.Serialize(transcript));

            var sample = SavedTranscriptScoring.ScoreFile(folder, Tool, Path.Combine(folder, "score.csv"));

            Assert.That(sample.ScenarioId, Does.StartWith("saved:"));
            Assert.That(sample.ScenarioType, Is.EqualTo(0));
            Assert.That(sample.Score!.FailureClass, Is.Not.EqualTo(FailureClass.ToolNotTried));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void TranscriptJson_OmitsScenarioId_WhenNull_AndWritesItWhenSet()
    {
        var unset = JsonSerializer.Serialize(new AgentTranscript());
        Assert.That(unset, Does.Not.Contain("ScenarioId"));

        var stamped = JsonSerializer.Serialize(new AgentTranscript { ScenarioId = "SomeScenario" });
        Assert.That(stamped, Does.Contain("\"ScenarioId\":\"SomeScenario\""));
        Assert.That(JsonSerializer.Deserialize<AgentTranscript>(stamped)!.ScenarioId, Is.EqualTo("SomeScenario"));
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "savedscore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // A run that read around with Search and stopped, never calling ReplaceSnippet (the tool the stop-adherence tool-error scenarios require).
    private static AgentTranscript NeverTriedTheToolTranscript(string? scenarioId)
    {
        var transcript = new AgentTranscript { ScenarioId = scenarioId };
        transcript.Turns.Add(Turn(1, Call("Search")));
        transcript.Turns.Add(Turn(2));
        return transcript;
    }

    [Test]
    [Explicit("No model needed. Run with roslynsentinel-modeleval.ps1 -Test ScoreTranscript -ScoreTranscript <transcript.json or run folder>")]
    public void ScoreSavedTranscript_AppendsOneLine()
    {
        var transcriptInput = Environment.GetEnvironmentVariable(SavedTranscriptScoring.TranscriptVariable);
        if (string.IsNullOrWhiteSpace(transcriptInput))
        {
            Assert.Ignore($"Set {SavedTranscriptScoring.TranscriptVariable} to a transcript.json or the run folder holding it.");
            return;
        }

        var tool = Environment.GetEnvironmentVariable(SavedTranscriptScoring.ToolVariable);
        if (string.IsNullOrWhiteSpace(tool))
        {
            tool = Tool;
        }

        var transcriptPath = TranscriptResume.ResolveTranscriptPath(transcriptInput);
        var csvPath = Environment.GetEnvironmentVariable(SavedTranscriptScoring.CsvVariable);
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            csvPath = Path.Combine(Path.GetDirectoryName(transcriptPath)!, "score.csv");
        }

        var sample = SavedTranscriptScoring.ScoreFile(transcriptPath, tool, csvPath);
        var score = sample.Score!;
        TestContext.Out.WriteLine(
            $"Appended {csvPath} | {tool}: FirstCallSucceeded={score.FirstCallSucceeded} FirstErrorCode={score.FirstErrorCode ?? "none"} " +
            $"CallsToRecover={score.CallsToRecover?.ToString() ?? "none"} MaxIdenticalCallRepeat={score.MaxIdenticalCallRepeat} " +
            $"RetryPhraseCount={score.RetryPhraseCount} FailureClass={score.FailureClass}");
    }

    private static AgentToolCallRecord Call(string toolName, string argumentsJson = "{}", bool isError = false, string? resultJson = null) => new()
    {
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
        ResultJson = resultJson ?? (isError ? NotFoundResultJson : "{\"isError\":false}"),
        IsError = isError,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };

    private static AgentTranscriptTurn Turn(int turnNumber, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = null, ReasoningContent = null },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = calls.ToList(),
    };
}
