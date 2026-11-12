using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// The pure gates of the resume scenarios: schema drift for the tools the kept turns called, and replay mismatch and
/// result drift. No live model.
/// </summary>
[TestFixture]
public class ResumeScenarioGateTests
{
    private const string CaptureCommit = "abc1234";
    private const string ReadFileDescription = "Reads a file from disk.";

    // ---- CheckSchemas ----

    [Test]
    public void CheckSchemas_IdenticalSchemas_ReportsNoDrift()
    {
        var source = Source(
            schemas: new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            descriptions: new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        var result = ResumeScenarioGate.CheckSchemas(
            source,
            [Turn(1, "ReadFile")],
            new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Is.Empty);
            Assert.That(result.SchemaDrift, Is.False);
        });
    }

    [Test]
    public void CheckSchemas_DescriptionOnlyChange_IsSchemaDriftButNotInconclusive()
    {
        var source = Source(
            schemas: new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            descriptions: new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        var result = ResumeScenarioGate.CheckSchemas(
            source,
            [Turn(1, "ReadFile")],
            new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            new Dictionary<string, string> { ["ReadFile"] = "Reads a file from disk, with line numbers." });

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Is.Empty);
            Assert.That(result.SchemaDrift, Is.True);
        });
    }

    [Test]
    public void CheckSchemas_StructuralChange_NamesToolAndCommit()
    {
        var source = Source(
            schemas: new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            descriptions: new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        var result = ResumeScenarioGate.CheckSchemas(
            source,
            [Turn(1, "ReadFile")],
            new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("integer") },
            new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Does.StartWith("SchemaChanged: "));
            Assert.That(result.InconclusiveReason, Does.Contain("ReadFile"));
            Assert.That(result.InconclusiveReason, Does.Contain(CaptureCommit));
            Assert.That(result.SchemaDrift, Is.False);
        });
    }

    [Test]
    public void CheckSchemas_NoSchemasRecorded_ReportsSchemaNotRecorded()
    {
        var source = Source(schemas: null, descriptions: null);

        var result = ResumeScenarioGate.CheckSchemas(
            source,
            [Turn(1, "ReadFile")],
            new Dictionary<string, JsonElement> { ["ReadFile"] = Schema("string") },
            new Dictionary<string, string> { ["ReadFile"] = ReadFileDescription });

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Does.StartWith("SchemaNotRecorded: "));
            Assert.That(result.SchemaDrift, Is.False);
        });
    }

    [Test]
    public void CheckSchemas_StructuralChangeToUncalledTool_IsIgnored()
    {
        var source = Source(
            schemas: new Dictionary<string, JsonElement>
            {
                ["ReadFile"] = Schema("string"),
                ["Build"] = Schema("string"),
            },
            descriptions: new Dictionary<string, string>
            {
                ["ReadFile"] = ReadFileDescription,
                ["Build"] = "Builds the solution.",
            });

        var result = ResumeScenarioGate.CheckSchemas(
            source,
            [Turn(1, "ReadFile")],
            new Dictionary<string, JsonElement>
            {
                ["ReadFile"] = Schema("string"),
                ["Build"] = Schema("integer"),
            },
            new Dictionary<string, string>
            {
                ["ReadFile"] = ReadFileDescription,
                ["Build"] = "Builds the solution.",
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Is.Empty);
            Assert.That(result.SchemaDrift, Is.False);
        });
    }

    // ---- CheckReplay ----

    [Test]
    public void CheckReplay_NoMismatchAndIdenticalText_IsClean()
    {
        var replayed = new List<ReplayedCall>
        {
            new(1, "ReadFile", false, false, "{\"ok\":true}", "{\"ok\":true}"),
        };

        var result = ResumeScenarioGate.CheckReplay(replayed);

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Is.Empty);
            Assert.That(result.ResultDrift, Is.Zero);
        });
    }

    [Test]
    public void CheckReplay_IsErrorMismatch_NamesReplayMismatch()
    {
        var replayed = new List<ReplayedCall>
        {
            new(1, "ReplaceSnippet", true, false, "{\"isError\":true}", "{\"isError\":false}"),
        };

        var result = ResumeScenarioGate.CheckReplay(replayed);

        Assert.That(result.InconclusiveReason, Does.StartWith("Replay mismatch:"));
    }

    [Test]
    public void CheckReplay_WordingChangeWithoutMismatch_CountsOneDrift()
    {
        var replayed = new List<ReplayedCall>
        {
            new(1, "ReplaceSnippet", true, true,
                "{\"message\":\"oldContent not found\"}",
                "{\"message\":\"oldContent was not found; nearest match at line 3\"}"),
        };

        var result = ResumeScenarioGate.CheckReplay(replayed);

        Assert.Multiple(() =>
        {
            Assert.That(result.InconclusiveReason, Is.Empty);
            Assert.That(result.ResultDrift, Is.EqualTo(1));
        });
    }

    // ---- helpers ----

    private static JsonElement Schema(string propertyType) =>
        JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{\"path\":{{\"type\":\"{propertyType}\"}}}}}}")
            .RootElement.Clone();

    private static ResumeSource Source(
        Dictionary<string, JsonElement>? schemas,
        Dictionary<string, string>? descriptions) => new()
        {
            CommitHash = CaptureCommit,
            ToolSchemas = schemas,
            ToolDescriptions = descriptions,
        };

    private static AgentTranscriptTurn Turn(int turnNumber, params string[] toolNames) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = "" },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = toolNames.Select(name => new AgentToolCallRecord
        {
            ToolCallId = $"call-{turnNumber}-{name}",
            ToolName = name,
            ArgumentsJson = "{}",
            ResultJson = "{}",
            IsError = false,
            Latency = TimeSpan.Zero,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
        }).ToList(),
    };
}
