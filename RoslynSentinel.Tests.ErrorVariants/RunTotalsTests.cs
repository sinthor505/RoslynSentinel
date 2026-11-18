using System.Text.Json;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class RunTotalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Test]
    public void FromTranscript_SumsLatenciesCallsAndTokensAcrossTurns()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(100), MakeMessage(input: 100, cached: 40, output: 10, reasoning: 4),
                MakeCall("{\"isError\":false}", isError: false, TimeSpan.FromMilliseconds(30)),
                MakeCall("{\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\"}}", isError: true, TimeSpan.FromMilliseconds(20))),
            MakeTurn(2, TimeSpan.FromMilliseconds(300), MakeMessage(input: 200, cached: 60, output: 30, reasoning: 6),
                MakeCall("{\"isError\":false}", isError: false, TimeSpan.FromMilliseconds(50))));

        var totals = RunTotals.FromTranscript(transcript, TimeSpan.FromSeconds(5));

        Assert.That(totals.TotalDuration, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(totals.TotalModelTime, Is.EqualTo(TimeSpan.FromMilliseconds(400)));
        Assert.That(totals.TotalToolTime, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.That(totals.TurnCount, Is.EqualTo(2));
        Assert.That(totals.ToolCallCount, Is.EqualTo(3));
        Assert.That(totals.ToolErrorCount, Is.EqualTo(1));
        Assert.That(totals.InputTokens, Is.EqualTo(300));
        Assert.That(totals.CachedInputTokens, Is.EqualTo(100));
        Assert.That(totals.NewInputTokens, Is.EqualTo(200));
        Assert.That(totals.OutputTokens, Is.EqualTo(40));
        Assert.That(totals.ReasoningTokens, Is.EqualTo(10));
        Assert.That(totals.AverageOutputTokensPerTurn, Is.EqualTo(20.0));
        Assert.That(totals.AverageReasoningTokensPerTurn, Is.EqualTo(5.0));
        Assert.That(totals.AverageTurnDuration, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
    }

    [Test]
    public void FromTranscript_TurnWithAllTokensNullIsSkippedInTheSums()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(10), MakeMessage(input: 100, cached: 10, output: 5, reasoning: 2)),
            MakeTurn(2, TimeSpan.FromMilliseconds(10), MakeMessage()));

        var totals = RunTotals.FromTranscript(transcript, TimeSpan.FromSeconds(1));

        Assert.That(totals.InputTokens, Is.EqualTo(100));
        Assert.That(totals.CachedInputTokens, Is.EqualTo(10));
        Assert.That(totals.NewInputTokens, Is.EqualTo(90));
        Assert.That(totals.OutputTokens, Is.EqualTo(5));
        Assert.That(totals.ReasoningTokens, Is.EqualTo(2));
        Assert.That(totals.AverageOutputTokensPerTurn, Is.EqualTo(5.0));
    }

    [Test]
    public void FromTranscript_ReasoningTokensAreNullWhenNoTurnReportsThem()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(10), MakeMessage(input: 100, cached: null, output: 5, reasoning: null)),
            MakeTurn(2, TimeSpan.FromMilliseconds(10), MakeMessage(input: 50, cached: null, output: 7, reasoning: null)));

        var totals = RunTotals.FromTranscript(transcript, TimeSpan.FromSeconds(1));

        Assert.That(totals.ReasoningTokens, Is.Null);
        Assert.That(totals.AverageReasoningTokensPerTurn, Is.Null);
        Assert.That(totals.InputTokens, Is.EqualTo(150));
        Assert.That(totals.NewInputTokens, Is.EqualTo(150));
    }

    [Test]
    public void FromTranscript_EmptyTranscriptHasZeroTurnsAndNullTokenTotals()
    {
        var totals = RunTotals.FromTranscript(new AgentTranscript { RunId = "run-empty" }, TimeSpan.FromSeconds(1));

        Assert.That(totals.TurnCount, Is.EqualTo(0));
        Assert.That(totals.TotalModelTime, Is.EqualTo(TimeSpan.Zero));
        Assert.That(totals.AverageTurnDuration, Is.EqualTo(TimeSpan.Zero));
        Assert.That(totals.InputTokens, Is.Null);
        Assert.That(totals.CachedInputTokens, Is.Null);
        Assert.That(totals.NewInputTokens, Is.Null);
        Assert.That(totals.OutputTokens, Is.Null);
        Assert.That(totals.ReasoningTokens, Is.Null);
        Assert.That(totals.AverageOutputTokensPerTurn, Is.Null);
        Assert.That(totals.AverageReasoningTokensPerTurn, Is.Null);
    }

    [Test]
    public void FromTranscript_CountsToolErrorCodesAndCompilerCodesPerErrorCall()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(10), MakeMessage(),
                MakeCall("{\"isError\":true,\"errorData\":{\"errorCode\":\"ValidationFailed\",\"message\":\"CS0103 and CS0103 again and CS1061\"}}", isError: true, TimeSpan.FromMilliseconds(1)),
                MakeCall("{\"isError\":true,\"errorData\":{\"errorCode\":\"ValidationFailed\",\"message\":\"CS0103 once\"}}", isError: true, TimeSpan.FromMilliseconds(1)),
                MakeCall("boom CS0246", isError: true, TimeSpan.FromMilliseconds(1)),
                MakeCall("{\"isError\":true,\"message\":\"no errorData here\"}", isError: true, TimeSpan.FromMilliseconds(1)),
                MakeCall("{\"isError\":false,\"message\":\"CS9999 in a passing result\"}", isError: false, TimeSpan.FromMilliseconds(1))));

        var totals = RunTotals.FromTranscript(transcript, TimeSpan.FromSeconds(1));

        var expectedToolErrors = new Dictionary<string, int> { ["ValidationFailed"] = 2, ["unknown"] = 2 };
        var expectedCompilerErrors = new Dictionary<string, int> { ["CS0103"] = 2, ["CS1061"] = 1, ["CS0246"] = 1 };
        Assert.That(totals.ToolErrorCount, Is.EqualTo(4));
        Assert.That(totals.ToolErrorCodes, Is.EqualTo(expectedToolErrors));
        Assert.That(totals.CompilerErrorCodes, Is.EqualTo(expectedCompilerErrors));
        Assert.That(totals.CompilerErrorCodes.ContainsKey("CS9999"), Is.False);
    }

    [Test]
    public void Serialize_WritesSummaryAsTheLastTopLevelPropertyWhenSet()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(10), MakeMessage(input: 1, cached: null, output: 1, reasoning: null),
                MakeCall("{}", isError: false, TimeSpan.FromMilliseconds(1))));
        transcript.Summary = RunTotals.FromTranscript(transcript, TimeSpan.FromSeconds(2));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(transcript, Indented));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        Assert.That(names[^1], Is.EqualTo("Summary"));
    }

    [Test]
    public void Serialize_OmitsSummaryWhenNull()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(10), MakeMessage(input: 1, cached: null, output: 1, reasoning: null)));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(transcript, Indented));

        Assert.That(document.RootElement.TryGetProperty("Summary", out _), Is.False);
    }

    [Test]
    public void Deserialize_RoundTripsSummaryAndSettings()
    {
        var transcript = BuildTranscript(
            MakeTurn(1, TimeSpan.FromMilliseconds(100), MakeMessage(input: 100, cached: 40, output: 10, reasoning: 4),
                MakeCall("{\"isError\":true,\"errorData\":{\"errorCode\":\"ValidationFailed\"}}", isError: true, TimeSpan.FromMilliseconds(20))),
            MakeTurn(2, TimeSpan.FromMilliseconds(300), MakeMessage(input: 200, cached: 60, output: 30, reasoning: 6)));
        transcript.ModelKey = "qwen/qwen3-coder-30b";
        transcript.ContextLength = 70144;
        transcript.Summary = RunTotals.FromTranscript(transcript, TimeSpan.FromMinutes(8));
        transcript.Settings = RunSettings.Build(
            0.2,
            0.95,
            new Dictionary<string, double> { ["topK"] = 20 },
            SamplerSettings.FromReported(0.2, 0.95, 0.0, null),
            new ModelLoadSettings { BaseUrl = "http://host:1234/v1", ContextLength = 70144, FlashAttention = true, SpeculativeDraftMtp = true, Parallel = 1 });

        var loaded = RoundTrip(transcript, out var json);

        Assert.That(loaded.Summary, Is.Not.Null);
        Assert.That(loaded.Summary!.TurnCount, Is.EqualTo(2));
        Assert.That(loaded.Summary.TotalModelTime, Is.EqualTo(TimeSpan.FromMilliseconds(400)));
        Assert.That(loaded.Summary.ToolErrorCodes, Is.EqualTo(new Dictionary<string, int> { ["ValidationFailed"] = 1 }));
        Assert.That(loaded.Summary.Backfilled, Is.Null);
        Assert.That(json, Does.Not.Contain("Backfilled"));

        Assert.That(loaded.Settings, Is.Not.Null);
        Assert.That(loaded.Settings!.Sampler.Temperature!.Source, Is.EqualTo(SamplerValue.SourceReported));
        Assert.That(loaded.Settings.Sampler.Temperature!.Verified, Is.True);
        Assert.That(loaded.Settings.Sampler.TopK!.Source, Is.EqualTo(SamplerValue.SourceDeclared));
        Assert.That(loaded.Settings.Sampler.TopK!.Verified, Is.False);
        Assert.That(loaded.Settings.Load!.FlashAttention, Is.True);
        Assert.That(loaded.Settings.Load.ContextLength, Is.EqualTo(70144));

        var settingsIndex = json.IndexOf("\"Settings\"", StringComparison.Ordinal);
        Assert.That(settingsIndex, Is.GreaterThan(json.IndexOf("\"ContextLength\"", StringComparison.Ordinal)));
        Assert.That(settingsIndex, Is.LessThan(json.IndexOf("\"Turns\"", StringComparison.Ordinal)));
    }

    [Test]
    public void Deserialize_ReadsABackfilledSummaryBlock()
    {
        const string literal = """{"RunId":"r","SystemPrompt":"","UserPrompt":"","Tools":[],"Turns":[],"Summary":{"TotalDuration":"00:08:01.9352293","TotalModelTime":"00:07:49.9342781","TotalToolTime":"00:00:11.7415288","TurnCount":22,"ToolCallCount":24,"ToolErrorCount":1,"InputTokens":452507,"CachedInputTokens":441439,"NewInputTokens":11068,"OutputTokens":3915,"ReasoningTokens":3021,"AverageOutputTokensPerTurn":177.95454545454547,"AverageReasoningTokensPerTurn":137.3181818181818,"AverageTurnDuration":"00:00:21.8943548","ToolErrorCodes":{"ValidationFailed":1},"CompilerErrorCodes":{"CS1061":1},"Backfilled":true}""";

        var transcript = JsonSerializer.Deserialize<AgentTranscript>(literal);

        Assert.That(transcript, Is.Not.Null);
        Assert.That(transcript!.Summary, Is.Not.Null);
        var summary = transcript.Summary!;
        Assert.That(summary.TurnCount, Is.EqualTo(22));
        Assert.That(summary.NewInputTokens, Is.EqualTo(11068));
        Assert.That(summary.TotalDuration, Is.EqualTo(TimeSpan.Parse("00:08:01.9352293")));
        Assert.That(summary.ToolErrorCodes["ValidationFailed"], Is.EqualTo(1));
        Assert.That(summary.CompilerErrorCodes["CS1061"], Is.EqualTo(1));
        Assert.That(summary.Backfilled, Is.True);
    }

    private static AgentTranscript RoundTrip(AgentTranscript written, out string json)
    {
        // Same serializer call as ModelAgentRunner.WriteTranscriptAsync.
        json = JsonSerializer.Serialize(written, new JsonSerializerOptions { WriteIndented = true });
        var folder = Path.Combine(Path.GetTempPath(), "RunTotalsTests_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "transcript.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, json);
            return SavedTranscript.Load(path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static AgentTranscript BuildTranscript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript { RunId = "run-1" };
        transcript.Turns.AddRange(turns);
        return transcript;
    }

    private static AgentChatMessage MakeMessage(int? input = null, int? cached = null, int? output = null, int? reasoning = null) => new()
    {
        Role = "assistant",
        Content = "reply",
        InputTokens = input,
        CachedInputTokens = cached,
        OutputTokens = output,
        ReasoningTokens = reasoning,
    };

    private static AgentToolCallRecord MakeCall(string resultJson, bool isError, TimeSpan latency) => new()
    {
        ToolName = "ReadFile",
        ArgumentsJson = "{\"filePath\":\"a.cs\"}",
        ResultJson = resultJson,
        IsError = isError,
        Latency = latency,
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(1),
        ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
    };

    private static AgentTranscriptTurn MakeTurn(int turnNumber, TimeSpan modelLatency, AgentChatMessage message, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = message,
        ModelLatency = modelLatency,
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(2),
        ToolCalls = [.. calls],
    };
}
