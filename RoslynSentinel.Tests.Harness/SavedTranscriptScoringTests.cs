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
