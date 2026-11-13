using System.Text.Json;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class SavedTranscriptTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void Load_RoundTripsATranscriptWrittenWithTheRunnersSerializerOptions()
    {
        var written = BuildTranscript();

        // Same serializer call as ModelAgentRunner.WriteTranscriptAsync (ModelAgentRunner.cs:910).
        var json = JsonSerializer.Serialize(written, new JsonSerializerOptions { WriteIndented = true });
        var folder = Path.Combine(Path.GetTempPath(), "SavedTranscriptTests_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "transcript.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, json);

            var loaded = SavedTranscript.Load(path);

            Assert.That(loaded.RunId, Is.EqualTo("run-1"));
            Assert.That(loaded.SystemPrompt, Is.EqualTo("system text"));
            Assert.That(loaded.UserPrompt, Is.EqualTo("user text"));
            Assert.That(loaded.Tools, Is.EqualTo(new[] { "ReplaceSnippet", "Build" }));
            Assert.That(loaded.CommitHash, Is.EqualTo("abc123"));
            Assert.That(loaded.ErrorPolicyMode, Is.EqualTo("pointer"));
            Assert.That(loaded.StopReason, Is.EqualTo(AgentStopReason.TargetReached));
            Assert.That(loaded.ToolDescriptions, Is.EqualTo(written.ToolDescriptions));
            Assert.That(loaded.ToolSchemas, Is.Not.Null);
            Assert.That(loaded.ToolSchemas!["ReplaceSnippet"].GetProperty("type").GetString(), Is.EqualTo("object"));

            Assert.That(loaded.Turns, Has.Count.EqualTo(2));

            var firstTurn = loaded.Turns[0];
            Assert.That(firstTurn.TurnNumber, Is.EqualTo(1));
            Assert.That(firstTurn.ModelMessage.Content, Is.EqualTo("first reply"));
            Assert.That(firstTurn.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(firstTurn.ToolCalls[0].ResultJson, Is.EqualTo("{\"ok\":true}"));
            Assert.That(firstTurn.ToolCalls[0].TrailingText, Is.EqualTo("trailing note"));
            Assert.That(firstTurn.ToolCalls[0].IsError, Is.False);

            var secondTurn = loaded.Turns[1];
            Assert.That(secondTurn.TurnNumber, Is.EqualTo(2));
            Assert.That(secondTurn.ModelMessage.Content, Is.EqualTo("second reply"));
            Assert.That(secondTurn.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(secondTurn.ToolCalls[0].ResultJson, Is.EqualTo("{\"ok\":false}"));
            Assert.That(secondTurn.ToolCalls[0].TrailingText, Is.Null);
            Assert.That(secondTurn.ToolCalls[0].IsError, Is.True);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void KeepTurns_TakesTurnsOneToKInOrder()
    {
        var turns = new List<AgentTranscriptTurn> { MakeTurn(1), MakeTurn(2), MakeTurn(3) };

        var kept = SavedTranscript.KeepTurns(turns, 2);

        Assert.That(kept.Select(turn => turn.TurnNumber), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void KeepTurns_RejectsZeroAndMoreThanTheTurnCount()
    {
        var turns = new List<AgentTranscriptTurn> { MakeTurn(1), MakeTurn(2), MakeTurn(3) };

        Assert.Throws<ArgumentOutOfRangeException>(() => SavedTranscript.KeepTurns(turns, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SavedTranscript.KeepTurns(turns, 4));
    }

    private static AgentTranscript BuildTranscript()
    {
        var transcript = new AgentTranscript
        {
            RunId = "run-1",
            SystemPrompt = "system text",
            UserPrompt = "user text",
            CommitHash = "abc123",
            ErrorPolicyMode = "pointer",
            StopReason = AgentStopReason.TargetReached,
            ToolDescriptions = new Dictionary<string, string>
            {
                ["ReplaceSnippet"] = "Replaces one snippet.",
                ["Build"] = "Builds the solution.",
            },
            ToolSchemas = new Dictionary<string, JsonElement>
            {
                ["ReplaceSnippet"] = Schema("{\"type\":\"object\"}"),
            },
        };
        transcript.Tools.Add("ReplaceSnippet");
        transcript.Tools.Add("Build");
        transcript.Turns.Add(new AgentTranscriptTurn
        {
            TurnNumber = 1,
            ModelMessage = new AgentChatMessage { Role = "assistant", Content = "first reply" },
            ModelLatency = TimeSpan.FromMilliseconds(40),
            StartedAt = Now,
            CompletedAt = Now,
            ToolCalls = [MakeCall("{\"ok\":true}", isError: false, trailingText: "trailing note")],
        });
        transcript.Turns.Add(new AgentTranscriptTurn
        {
            TurnNumber = 2,
            ModelMessage = new AgentChatMessage { Role = "assistant", Content = "second reply" },
            ModelLatency = TimeSpan.FromMilliseconds(55),
            StartedAt = Now,
            CompletedAt = Now,
            ToolCalls = [MakeCall("{\"ok\":false}", isError: true, trailingText: null)],
        });
        return transcript;
    }

    private static AgentToolCallRecord MakeCall(string resultJson, bool isError, string? trailingText) => new()
    {
        ToolName = "ReplaceSnippet",
        ArgumentsJson = "{\"filePath\":\"a.cs\"}",
        ResultJson = resultJson,
        IsError = isError,
        Latency = TimeSpan.FromMilliseconds(12),
        StartedAt = Now,
        CompletedAt = Now,
        ToolCallId = "call-" + resultJson.GetHashCode().ToString("X"),
        TrailingText = trailingText,
    };

    private static AgentTranscriptTurn MakeTurn(int turnNumber) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = "reply " + turnNumber },
        ModelLatency = TimeSpan.FromMilliseconds(10),
        StartedAt = Now,
        CompletedAt = Now,
    };

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
