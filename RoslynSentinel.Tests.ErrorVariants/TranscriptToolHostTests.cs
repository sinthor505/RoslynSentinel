using System.Text.Json;
using ModelContextProtocol.Protocol;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class TranscriptToolHostTests
{
    private const string FileMissF = "FileNotFound.armF.sample-01.transcript.json";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    // ---- matched read calls ----

    [Test]
    public async Task CallTool_MatchedReadCall_ReturnsTheRecordedBytes_EvenWithKeysInAnotherOrderAndAnotherReason()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[1].ToolCalls[0]; // turn 2: a recorded Search, no trailing text
        Assert.That(recorded.ToolName, Is.EqualTo("Search"));
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        // Keys reversed against the recorded order, and a different free-text reason: still the same call.
        var recordedArguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(recorded.ArgumentsJson)!;
        var arguments = new Dictionary<string, object?>();
        foreach (var pair in recordedArguments.Reverse())
        {
            arguments[pair.Key] = pair.Key == "reason" ? "a different justification" : pair.Value;
        }

        var result = await host.CallToolAsync("Search", arguments, CancellationToken.None);

        Assert.That(host.OffScript, Is.False);
        Assert.That(result.IsError, Is.EqualTo(recorded.IsError));
        Assert.That(Blocks(result), Is.EqualTo(new[] { recorded.ResultJson }));
    }

    [Test]
    public async Task CallTool_MatchedCallWithTrailingText_ReturnsTwoBlocksAndTheRecordedIsError()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[4].ToolCalls[0]; // turn 5: an erroring Search symbol with a trailing block
        Assert.That(recorded.IsError, Is.True);
        Assert.That(recorded.TrailingText, Is.Not.Null.And.Not.Empty);
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var result = await host.CallToolAsync("Search", RunnerArguments(recorded.ArgumentsJson), CancellationToken.None);

        Assert.That(host.OffScript, Is.False);
        Assert.That(result.IsError, Is.True);
        Assert.That(Blocks(result), Is.EqualTo(new[] { recorded.ResultJson, recorded.TrailingText! }));
        // The runner rebuilds ResultJson = first block, TrailingText = the rest joined by a blank line.
        Assert.That(string.Join("\n\n", Blocks(result).Skip(1)), Is.EqualTo(recorded.TrailingText));
    }

    [Test]
    public async Task CallTool_ArgumentsAsTheRunnerBuildsThem_JsonElementValues_Match()
    {
        var recorded = MakeCall("Search", "{\"mode\":\"text\",\"query\":\"X\",\"maxResults\":10,\"reason\":\"r1\"}", "{\"isError\":false,\"n\":1}", false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, recorded)), cutTurn: 1);

        // maxResults as 10.0 and keys reordered: the JSON text a model could equally have produced.
        var result = await host.CallToolAsync(
            "Search", RunnerArguments("{\"reason\":\"r2\",\"maxResults\":10.0,\"query\":\"X\",\"mode\":\"text\"}"), CancellationToken.None);

        Assert.That(host.OffScript, Is.False);
        Assert.That(Blocks(result), Is.EqualTo(new[] { recorded.ResultJson }));
    }

    [Test]
    public async Task CallTool_PrimitiveAndNestedDictionaryArguments_CanonicalizeLikeTheRecordedJson()
    {
        var recorded = MakeCall(
            "Search",
            "{\"a\":1,\"b\":{\"y\":[1,2.5],\"x\":\"s\"},\"batchEdits\":[{\"reason\":\"r\",\"oldContent\":\"o\"}],\"reason\":\"r\"}",
            "{\"isError\":false}",
            false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, recorded)), cutTurn: 1);

        var arguments = new Dictionary<string, object?>
        {
            ["batchEdits"] = new object?[] { new Dictionary<string, object?> { ["oldContent"] = "o", ["reason"] = "other" } },
            ["b"] = new Dictionary<string, object?> { ["x"] = "s", ["y"] = new object?[] { 1.0, 2.5m } },
            ["a"] = 1L,
        };

        var result = await host.CallToolAsync("Search", arguments, CancellationToken.None);

        Assert.That(host.OffScript, Is.False);
        Assert.That(Blocks(result), Is.EqualTo(new[] { recorded.ResultJson }));
    }

    [Test]
    public async Task CallTool_DifferentStringValue_IsNotAMatch()
    {
        var recorded = MakeCall("Search", "{\"query\":\"Abc\"}", "{\"isError\":false}", false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, recorded)), cutTurn: 1);

        await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"abc\"}"), CancellationToken.None);

        Assert.That(host.OffScript, Is.True);
    }

    // ---- off-script ----

    [Test]
    public async Task CallTool_UnmatchedReadCall_ReturnsThePlaceholderAndGoesOffScript()
    {
        var transcript = Load(FileMissF);
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var result = await host.CallToolAsync("Search", RunnerArguments("{\"mode\":\"text\",\"query\":\"never recorded\"}"), CancellationToken.None);

        Assert.That(host.OffScript, Is.True);
        Assert.That(host.OffScriptTool, Is.EqualTo("Search"));
        Assert.That(host.OffScriptCallIndex, Is.EqualTo(0));
        Assert.That(result.IsError, Is.False);
        Assert.That(Blocks(result), Is.EqualTo(new[] { TranscriptToolHost.PlaceholderText }));
    }

    [Test]
    public async Task CallTool_WriteTool_GoesOffScriptEvenWhenARecordedCallMatches()
    {
        var recorded = MakeCall("ReplaceSnippet", "{\"filePath\":\"a.cs\"}", "{\"isError\":false}", false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, recorded)), cutTurn: 1);

        var result = await host.CallToolAsync("ReplaceSnippet", RunnerArguments(recorded.ArgumentsJson), CancellationToken.None);

        Assert.That(host.OffScript, Is.True);
        Assert.That(host.OffScriptTool, Is.EqualTo("ReplaceSnippet"));
        Assert.That(Blocks(result), Is.EqualTo(new[] { TranscriptToolHost.PlaceholderText }));
    }

    [Test]
    public async Task CallTool_UnknownTool_GoesOffScript()
    {
        var recorded = MakeCall("BrandNewTool", "{}", "{\"isError\":false}", false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, recorded)), cutTurn: 1);

        await host.CallToolAsync("BrandNewTool", RunnerArguments("{}"), CancellationToken.None);

        Assert.That(host.OffScript, Is.True);
        Assert.That(host.OffScriptTool, Is.EqualTo("BrandNewTool"));
    }

    [Test]
    public async Task CallTool_FirstOffScriptEventWins_AndCountsTheScriptedCallsBeforeIt()
    {
        var scripted = MakeCall("Search", "{\"query\":\"A\"}", "{\"isError\":false}", false);
        var host = new TranscriptToolHost(BuildTranscript(MakeTurn(1), MakeTurn(2, scripted)), cutTurn: 1);

        await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"A\"}"), CancellationToken.None);
        await host.CallToolAsync("Build", RunnerArguments("{}"), CancellationToken.None);
        await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"unrecorded\"}"), CancellationToken.None);
        // A later scripted call still works and leaves the off-script state alone.
        var later = await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"A\"}"), CancellationToken.None);

        Assert.That(host.OffScriptTool, Is.EqualTo("Build"));
        Assert.That(host.OffScriptCallIndex, Is.EqualTo(1));
        Assert.That(host.CallCount, Is.EqualTo(4));
        Assert.That(Blocks(later), Is.EqualTo(new[] { scripted.ResultJson }));
    }

    // ---- the cut ----

    [Test]
    public async Task CallTool_RecordedCallAtTheCut_IsNotServed()
    {
        var transcript = Load(FileMissF);
        var turnTwoCall = transcript.Turns[1].ToolCalls[0];

        // Cut after turn 2: that Search is a seed turn now, not an answer the host may give.
        var atCut = new TranscriptToolHost(transcript, cutTurn: 2);
        await atCut.CallToolAsync("Search", RunnerArguments(turnTwoCall.ArgumentsJson), CancellationToken.None);
        Assert.That(atCut.OffScript, Is.True);

        // Cut after turn 1: the same call is after the cut and is served.
        var beforeIt = new TranscriptToolHost(transcript, cutTurn: 1);
        await beforeIt.CallToolAsync("Search", RunnerArguments(turnTwoCall.ArgumentsJson), CancellationToken.None);
        Assert.That(beforeIt.OffScript, Is.False);
    }

    [Test]
    public async Task CallTool_RecordedCallBeforeTheCut_IsNotServed()
    {
        var transcript = Load(FileMissF);
        var turnOneCall = transcript.Turns[0].ToolCalls[0]; // the erroring ReadFile of the seed
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        await host.CallToolAsync("ReadFile", RunnerArguments(turnOneCall.ArgumentsJson), CancellationToken.None);

        Assert.That(host.OffScript, Is.True);
        Assert.That(host.OffScriptTool, Is.EqualTo("ReadFile"));
    }

    [Test]
    public void Constructor_CutTurnOutOfRange_Throws()
    {
        var transcript = BuildTranscript(MakeTurn(1), MakeTurn(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new TranscriptToolHost(transcript, cutTurn: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new TranscriptToolHost(transcript, cutTurn: 3));
    }

    // ---- sticky override ----

    [Test]
    public async Task CallTool_StickyOverride_ServedResultWithTheOverriddenCodeAndMessage_GetsTheVariant()
    {
        var transcript = StickyTranscript();
        var variant = new ErrorVariant("v", "X", "variant message", "variant trailing", "000000000000");
        var host = new TranscriptToolHost(transcript, cutTurn: 1, variant);

        var result = await host.CallToolAsync("ReadFile", RunnerArguments("{\"filePath\":\"b.cs\"}"), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(Blocks(result), Is.EqualTo(new[] { ErrorEnvelope("X", "variant message"), "variant trailing" }));
    }

    [Test]
    public async Task CallTool_StickyOverride_ADifferentMessageOrCode_StaysAsRecorded()
    {
        var transcript = StickyTranscript();
        var variant = new ErrorVariant("v", "X", "variant message", "variant trailing", "000000000000");
        var host = new TranscriptToolHost(transcript, cutTurn: 1, variant);

        var differentMessage = await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"q\"}"), CancellationToken.None);
        var differentCode = await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"other code\"}"), CancellationToken.None);

        Assert.That(Blocks(differentMessage), Is.EqualTo(new[] { ErrorEnvelope("X", "other"), "recorded trailing" }));
        Assert.That(Blocks(differentCode), Is.EqualTo(new[] { ErrorEnvelope("Y", "first"), "recorded trailing" }));
    }

    [Test]
    public async Task CallTool_NoVariant_ServesEverythingAsRecorded()
    {
        var host = new TranscriptToolHost(StickyTranscript(), cutTurn: 1, variant: null);

        var result = await host.CallToolAsync("ReadFile", RunnerArguments("{\"filePath\":\"b.cs\"}"), CancellationToken.None);

        Assert.That(Blocks(result), Is.EqualTo(new[] { ErrorEnvelope("X", "first"), "recorded trailing" }));
    }

    [Test]
    public async Task CallTool_StickyOverride_NullTrailingKeepsTheRecordedBlock_EmptyRemovesIt_AndSolutionTokenIsReplaced()
    {
        var keep = new TranscriptToolHost(
            StickyTranscript(), 1, new ErrorVariant("v", "X", "variant message", null, "000000000000"));
        var kept = await keep.CallToolAsync("ReadFile", RunnerArguments("{\"filePath\":\"b.cs\"}"), CancellationToken.None);
        Assert.That(Blocks(kept), Is.EqualTo(new[] { ErrorEnvelope("X", "variant message"), "recorded trailing" }));

        var remove = new TranscriptToolHost(
            StickyTranscript(), 1, new ErrorVariant("v", "X", "variant message", "", "000000000000"));
        var removed = await remove.CallToolAsync("ReadFile", RunnerArguments("{\"filePath\":\"b.cs\"}"), CancellationToken.None);
        Assert.That(Blocks(removed), Is.EqualTo(new[] { ErrorEnvelope("X", "variant message") }));

        var token = new TranscriptToolHost(
            StickyTranscript(), 1, new ErrorVariant("v", "X", "in {SOLUTION} here", "see {SOLUTION}", "000000000000"), "C:\\fx\\dir");
        var tokenResult = await token.CallToolAsync("ReadFile", RunnerArguments("{\"filePath\":\"b.cs\"}"), CancellationToken.None);
        var blocks = Blocks(tokenResult);
        Assert.That(ResultOverride.TryReadError(blocks[0], out _, out var message), Is.True);
        Assert.That(message, Is.EqualTo("in C:\\fx\\dir here"));
        Assert.That(blocks[1], Is.EqualTo("see C:\\fx\\dir"));
    }

    [Test]
    public void Constructor_VariantForAnotherErrorCode_Throws()
    {
        var variant = new ErrorVariant("v", "Z", "variant message", null, "000000000000");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = new TranscriptToolHost(StickyTranscript(), 1, variant));

        Assert.That(ex!.Message, Does.Contain("'X'").And.Contain("'Z'"));
    }

    [Test]
    public void Constructor_VariantWhenTheCutTurnHasNoErrorCall_Throws()
    {
        var transcript = BuildTranscript(MakeTurn(1, MakeCall("Search", "{}", "{\"isError\":false}", false)));
        var variant = new ErrorVariant("v", "X", "variant message", null, "000000000000");

        Assert.Throws<InvalidOperationException>(() => _ = new TranscriptToolHost(transcript, 1, variant));
    }

    // ---- repeat policy ----

    [Test]
    public async Task CallTool_SameCallRecordedSeveralTimes_ServesTheFirstUnservedThenRepeatsTheLast()
    {
        var first = MakeCall("Search", "{\"query\":\"Q\"}", "{\"isError\":false,\"n\":1}", false);
        var second = MakeCall("Search", "{\"query\":\"Q\"}", "{\"isError\":false,\"n\":2}", false);
        var transcript = BuildTranscript(MakeTurn(1), MakeTurn(2, first), MakeTurn(3, second));
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var served = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var result = await host.CallToolAsync("Search", RunnerArguments("{\"query\":\"Q\"}"), CancellationToken.None);
            served.Add(Blocks(result)[0]);
        }

        Assert.That(host.OffScript, Is.False);
        Assert.That(served, Is.EqualTo(new[] { first.ResultJson, second.ResultJson, second.ResultJson, second.ResultJson }));
    }

    // ---- tool list ----

    [Test]
    public async Task ListTools_BuildsTheHeaderToolsInOrderWithDescriptionAndSchema()
    {
        var transcript = Load(FileMissF);
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var tools = await host.ListToolsAsync(CancellationToken.None);

        Assert.That(tools.Select(t => t.Name), Is.EqualTo(transcript.Tools));
        Assert.That(tools, Has.Count.EqualTo(55));
        foreach (var tool in tools)
        {
            Assert.That(tool.Description, Is.EqualTo(transcript.ToolDescriptions![tool.Name]), tool.Name);
            Assert.That(tool.ParametersSchema.ValueKind, Is.EqualTo(JsonValueKind.Object), tool.Name);
            Assert.That(tool.ParametersSchema.GetRawText(), Is.EqualTo(transcript.ToolSchemas![tool.Name].GetRawText()), tool.Name);
        }
    }

    [Test]
    public async Task ListTools_ToolDescriptionsNull_IsRefused()
    {
        var transcript = ToolListTranscript();
        transcript.ToolDescriptions = null;
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListToolsAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("ToolDescriptions"));
    }

    [Test]
    public async Task ListTools_ToolDescriptionsEmpty_IsRefused()
    {
        var transcript = ToolListTranscript();
        transcript.ToolDescriptions = [];
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListToolsAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("ToolDescriptions"));
    }

    [Test]
    public async Task ListTools_ASchemaIsMissing_IsRefusedNamingTheTool()
    {
        var transcript = ToolListTranscript();
        transcript.ToolSchemas!.Remove("Search");
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListToolsAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("'Search'").And.Contain("ToolSchemas"));
    }

    [Test]
    public async Task ListTools_ToolSchemasNull_IsRefused()
    {
        var transcript = ToolListTranscript();
        transcript.ToolSchemas = null;
        var host = new TranscriptToolHost(transcript, cutTurn: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ListToolsAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("ToolSchemas"));
    }

    // ---- helpers ----

    private static AgentTranscript Load(string fileName) =>
        SavedTranscript.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

    private static List<string> Blocks(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToList();

    // The arguments dictionary exactly as ModelAgentRunner.ExecuteToolCallAsync builds it.
    private static Dictionary<string, object?> RunnerArguments(string argumentsJson) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];

    private static string ErrorEnvelope(string errorCode, string rawMessage) =>
        "{\"isError\":true,\"errorData\":{\"errorCode\":\"" + errorCode + "\",\"message\":\"" + rawMessage + "\"},\"findings\":[]}";

    // Turn 1 holds the overridden call (X / "first"). After the cut: a ReadFile b.cs with the same code and message and a
    // trailing block, a Search q with the same code but another message, and a Search "other code" with another code.
    private static AgentTranscript StickyTranscript() => BuildTranscript(
        MakeTurn(1, MakeCall("ReadFile", "{\"filePath\":\"a.cs\"}", ErrorEnvelope("X", "first"), true)),
        MakeTurn(
            2,
            MakeCall("ReadFile", "{\"filePath\":\"b.cs\"}", ErrorEnvelope("X", "first"), true, "recorded trailing"),
            MakeCall("Search", "{\"query\":\"q\"}", ErrorEnvelope("X", "other"), true, "recorded trailing"),
            MakeCall("Search", "{\"query\":\"other code\"}", ErrorEnvelope("Y", "first"), true, "recorded trailing")));

    private static AgentTranscript ToolListTranscript()
    {
        var transcript = BuildTranscript(MakeTurn(1, MakeCall("ReadFile", "{}", "{\"isError\":false}", false)));
        transcript.Tools.AddRange(["ReadFile", "Search"]);
        transcript.ToolDescriptions = new Dictionary<string, string> { ["ReadFile"] = "reads", ["Search"] = "searches" };
        transcript.ToolSchemas = new Dictionary<string, JsonElement>
        {
            ["ReadFile"] = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            ["Search"] = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone(),
        };
        return transcript;
    }

    private static AgentTranscript BuildTranscript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript { RunId = "run-1" };
        transcript.Turns.AddRange(turns);
        return transcript;
    }

    private static AgentToolCallRecord MakeCall(string toolName, string argumentsJson, string resultJson, bool isError, string? trailingText = null) => new()
    {
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
        ResultJson = resultJson,
        IsError = isError,
        Latency = TimeSpan.FromMilliseconds(12),
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(1),
        ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
        TrailingText = trailingText,
    };

    private static AgentTranscriptTurn MakeTurn(int turnNumber, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = "reply " + turnNumber },
        ModelLatency = TimeSpan.FromMilliseconds(40),
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(2),
        ToolCalls = [.. calls],
    };
}
