using System.Reflection;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class ResultOverrideTests
{
    private const string FileMissF = "FileNotFound.armF.sample-01.transcript.json";
    private const string FileMissE = "FileNotFound.armE.sample-01.transcript.json";
    private const string FileMissG = "FileNotFound.armG.sample-01.transcript.json";
    private const string SearchMiss = "NotFound.search-symbol-miss.sample-01.transcript.json";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    // ---- identity on the real samples ----

    [Test]
    public void Apply_UnchangedMessageOnReadFileMiss_GivesByteIdenticalResult()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        var variant = IdentityVariant(recorded);

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        AssertIdentical(turns[0].ToolCalls[0], recorded);
    }

    [Test]
    public void Apply_UnchangedMessageOnSearchSymbolMiss_GivesByteIdenticalResultInTurnTwo()
    {
        var transcript = Load(SearchMiss);
        var recorded = transcript.Turns[1].ToolCalls[0];
        var variant = IdentityVariant(recorded);

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(turns, Has.Count.EqualTo(2));
        AssertIdentical(turns[1].ToolCalls[0], recorded);
    }

    [TestCase("armE", FileMissE)]
    [TestCase("armF", FileMissF)]
    [TestCase("armG", FileMissG)]
    public void Apply_HarvestedArmFile_IsByteIdenticalOnItsOwnRun(string armId, string transcriptFile)
    {
        var transcript = Load(transcriptFile);
        var recorded = transcript.Turns[0].ToolCalls[0];
        var variant = ErrorVariantLoader.Load("FileNotFound", armId);

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        AssertIdentical(turns[0].ToolCalls[0], recorded);
    }

    [Test]
    public void Apply_ReturnsTheHashOfTheDecodedRecordedMessage()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        Assert.That(ResultOverride.TryReadError(recorded.ResultJson, out _, out var recordedMessage), Is.True);
        var variant = IdentityVariant(recorded) with { Message = "something else" };

        var (_, hash) = ResultOverride.Apply(transcript, variant);

        Assert.That(hash, Is.EqualTo(ErrorVariantLoader.HashOf(recordedMessage)));
        Assert.That(hash, Has.Length.EqualTo(12));
    }

    // ---- trailing semantics ----

    [Test]
    public void Apply_TrailingNull_KeepsTheRecordedTrailingText()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        Assert.That(recorded.TrailingText, Is.Not.Null);
        var variant = IdentityVariant(recorded) with { Message = "changed", Trailing = null };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(turns[0].ToolCalls[0].TrailingText, Is.EqualTo(recorded.TrailingText));
    }

    [Test]
    public void Apply_TrailingEmpty_RemovesTheTrailingText()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]) with { Trailing = "" };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(turns[0].ToolCalls[0].TrailingText, Is.Null);
    }

    [Test]
    public void Apply_TrailingText_ReplacesTheTrailingText()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]) with { Trailing = "new trailing" };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(turns[0].ToolCalls[0].TrailingText, Is.EqualTo("new trailing"));
    }

    // ---- the splice touches only the message span ----

    [Test]
    public void SpliceMessage_ChangesOnlyTheMessageValue_OnASyntheticEnvelope()
    {
        const string recorded = "{\"toolCall\":{\"name\":\"T\"},\"isError\":true,\"errorData\":{\"errorCode\":\"X\",\"message\":\"old text\",\"extra\":1},\"tail\":[1, 2]}";

        var result = ResultOverride.SpliceMessage(recorded, "new text");

        Assert.That(result, Is.EqualTo(recorded.Replace("old text", "new text")));
    }

    [Test]
    public void Apply_ReplacedMessageOnRealSample_LeavesEveryByteOutsideTheMessageSpan()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        var variant = IdentityVariant(recorded) with { Message = "short replacement" };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        var result = turns[0].ToolCalls[0].ResultJson;
        const string messageOpen = "\"message\":\"";
        var start = recorded.ResultJson.IndexOf(messageOpen, StringComparison.Ordinal) + messageOpen.Length;
        var suffixStart = recorded.ResultJson.IndexOf("\"},\"findings\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(messageOpen.Length));
        Assert.That(suffixStart, Is.GreaterThan(start));
        Assert.That(result, Is.EqualTo(recorded.ResultJson[..start] + "short replacement" + recorded.ResultJson[suffixStart..]));
    }

    // ---- escaping ----

    [Test]
    public void SpliceMessage_KeepsAngleBracketsApostropheBackslashQuoteAndNonAsciiAsTheServerWritesThem()
    {
        const string recorded = "{\"isError\":true,\"errorData\":{\"errorCode\":\"X\",\"message\":\"old\"},\"hasMoreData\":false}";
        const string text = "a < b > c & d 'q' \"quoted\" C:\\dir\\file.cs caf\u00e9 \u65e5\u672c\ttab";

        var result = ResultOverride.SpliceMessage(recorded, text);

        // Relaxed escaping: <, >, &, ' and non-ASCII stay literal; quote, backslash and control characters are escaped.
        Assert.That(result, Does.Contain("a < b > c & d 'q' \\\"quoted\\\" C:\\\\dir\\\\file.cs caf\u00e9 \u65e5\u672c\\ttab"));
        Assert.That(result, Does.Not.Contain("\\u003C"));
        Assert.That(ResultOverride.TryReadError(result, out var code, out var message), Is.True);
        Assert.That(code, Is.EqualTo("X"));
        Assert.That(message, Is.EqualTo(text));
        Assert.That(result, Does.EndWith("\"},\"hasMoreData\":false}"));
    }

    [Test]
    public void Apply_VariantWithSpecialCharacters_RoundTripsThroughTheRealSample()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        const string text = "File 'a<b>.cs' in C:\\x\\y with \"quotes\" and caf\u00e9";
        var variant = IdentityVariant(recorded) with { Message = text };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        var result = turns[0].ToolCalls[0].ResultJson;
        using var document = JsonDocument.Parse(result);
        Assert.That(document.RootElement.GetProperty("errorData").GetProperty("message").GetString(), Is.EqualTo(text));
        Assert.That(result, Does.Contain("'a<b>.cs'"));
        Assert.That(document.RootElement.GetProperty("solutionRoot").GetString(),
            Is.EqualTo(JsonDocument.Parse(recorded.ResultJson).RootElement.GetProperty("solutionRoot").GetString()));
    }

    [Test]
    public void SpliceMessage_RejectsARecordedMessageWithEscapingTheRelaxedEncoderDoesNotProduce()
    {
        // A default (HTML-safe) encoder would have written \u003C for '<'.
        const string recorded = "{\"isError\":true,\"errorData\":{\"errorCode\":\"X\",\"message\":\"a \\u003C b\"}}";

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.SpliceMessage(recorded, "new"));

        Assert.That(ex!.Message, Does.Contain("escaping"));
    }

    [Test]
    public void SpliceMessage_RejectsAResultWithoutAnErrorDataMessage()
    {
        Assert.Throws<InvalidOperationException>(() => ResultOverride.SpliceMessage("{\"isError\":false}", "x"));
        Assert.Throws<InvalidOperationException>(() => ResultOverride.SpliceMessage("not json", "x"));
    }

    // ---- {SOLUTION} ----

    [Test]
    public void Apply_ReplacesTheSolutionTokenInMessageAndTrailing()
    {
        var transcript = Load(FileMissF);
        const string solution = "C:\\Temp\\RoslynSentinelTests_abc";
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]) with
        {
            Message = "not found under {SOLUTION}\\a.cs",
            Trailing = "look in {SOLUTION}",
        };

        var (turns, _) = ResultOverride.Apply(transcript, variant, solutionDirectory: solution);

        var call = turns[0].ToolCalls[0];
        Assert.That(ResultOverride.TryReadError(call.ResultJson, out _, out var message), Is.True);
        Assert.That(message, Is.EqualTo("not found under C:\\Temp\\RoslynSentinelTests_abc\\a.cs"));
        Assert.That(call.TrailingText, Is.EqualTo("look in C:\\Temp\\RoslynSentinelTests_abc"));
    }

    [Test]
    public void Apply_LeavesTheSolutionTokenWhenNoDirectoryIsGiven()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]) with
        {
            Message = "under {SOLUTION}",
            Trailing = "also {SOLUTION}",
        };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        var call = turns[0].ToolCalls[0];
        Assert.That(ResultOverride.TryReadError(call.ResultJson, out _, out var message), Is.True);
        Assert.That(message, Is.EqualTo("under {SOLUTION}"));
        Assert.That(call.TrailingText, Is.EqualTo("also {SOLUTION}"));
    }

    // ---- refusals ----

    [Test]
    public void Apply_RefusesAnErrorCodeThatDiffersFromTheVariants()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]) with { ErrorCode = "NotFound" };

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.Apply(transcript, variant));

        Assert.That(ex!.Message, Does.Contain("FileNotFound").And.Contain("NotFound").And.Contain("Turn 1"));
    }

    [Test]
    public void Apply_RefusesACutTurnWithNoErrorCall()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]);

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.Apply(transcript, variant, cutTurn: 2));

        Assert.That(ex!.Message, Does.Contain("Turn 2").And.Contain("0 call(s)"));
    }

    [Test]
    public void Apply_RefusesATranscriptWithoutAnyErrorTurnWhenTheCutIsDefaulted()
    {
        var transcript = BuildTranscript(MakeTurn(1, MakeCall("{\"isError\":false}", false)));

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.Apply(transcript, SyntheticVariant("X")));

        Assert.That(ex!.Message, Does.Contain("No turn"));
    }

    [Test]
    public void Apply_RefusesACutTurnWithSeveralErrorCalls()
    {
        var transcript = BuildTranscript(MakeTurn(1,
            MakeCall(ErrorEnvelope("X", "first"), true),
            MakeCall(ErrorEnvelope("X", "second"), true)));

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.Apply(transcript, SyntheticVariant("X")));

        Assert.That(ex!.Message, Does.Contain("Turn 1").And.Contain("2 call(s)"));
    }

    [Test]
    public void Apply_RefusesAnIsErrorFlagThatDisagreesWithTheResultJson()
    {
        var transcript = BuildTranscript(MakeTurn(1, MakeCall(ErrorEnvelope("X", "first"), false)));

        var ex = Assert.Throws<InvalidOperationException>(() => ResultOverride.Apply(transcript, SyntheticVariant("X")));

        Assert.That(ex!.Message, Does.Contain("IsError=False"));
    }

    [Test]
    public void Apply_RefusesAnExplicitCutTurnOutOfRange()
    {
        var transcript = Load(FileMissF);
        var variant = IdentityVariant(transcript.Turns[0].ToolCalls[0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => ResultOverride.Apply(transcript, variant, cutTurn: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResultOverride.Apply(transcript, variant, cutTurn: 99));
    }

    // ---- cut, copy ----

    [Test]
    public void Apply_DropsTurnsAfterTheCutAndLeavesTheSourceTranscriptUntouched()
    {
        var transcript = Load(FileMissF);
        var recorded = transcript.Turns[0].ToolCalls[0];
        var variant = IdentityVariant(recorded) with { Message = "changed" };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(transcript.Turns, Has.Count.EqualTo(6));
        Assert.That(turns, Has.Count.EqualTo(1));
        Assert.That(turns[0].TurnNumber, Is.EqualTo(1));
        Assert.That(transcript.Turns[0].ToolCalls[0], Is.SameAs(recorded));
        Assert.That(transcript.Turns[0].ToolCalls[0].ResultJson, Does.Not.Contain("changed"));
    }

    [Test]
    public void Apply_KeepsEarlierTurnsAsTheSameInstancesWhenTheCutIsLater()
    {
        var transcript = Load(SearchMiss);
        var variant = IdentityVariant(transcript.Turns[1].ToolCalls[0]);

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        Assert.That(turns[0], Is.SameAs(transcript.Turns[0]));
        Assert.That(turns[1], Is.Not.SameAs(transcript.Turns[1]));
    }

    [Test]
    public void Apply_CarriesTheAssistsOfTheCutTurnForward()
    {
        var assists = new List<AgentAssist> { new("collapse", "ReadFile", 3, "assist text") };
        var turn = MakeTurn(1, MakeCall(ErrorEnvelope("X", "first"), true));
        turn.Assists = assists;
        var transcript = BuildTranscript(turn);

        var (turns, _) = ResultOverride.Apply(transcript, SyntheticVariant("X"));

        Assert.That(turns[0].Assists, Is.Not.Null);
        Assert.That(turns[0].Assists, Is.EqualTo(assists));
    }

    [Test]
    public void Apply_CopiesEveryPublicPropertyOfTheTurnAndTheCallThatTheVariantDoesNotTarget()
    {
        // A future field on AgentTranscriptTurn or AgentToolCallRecord that CopyTurn/CopyCall forgets shows up here:
        // every property is populated non-default on the original, then compared one by one.
        var turn = MakeTurn(1, MakeCall(ErrorEnvelope("X", "first"), true), MakeCall("{\"isError\":false}", false));
        turn.Assists = [new AgentAssist("injection", "Search", 2, "text")];
        var transcript = BuildTranscript(turn);
        var originalCall = turn.ToolCalls[0];
        var variant = SyntheticVariant("X") with { Message = "different", Trailing = "different trailing" };

        var (turns, _) = ResultOverride.Apply(transcript, variant);

        var rewrittenTurn = turns[0];
        var rewrittenCall = rewrittenTurn.ToolCalls[0];

        foreach (var property in typeof(AgentTranscriptTurn).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var original = property.GetValue(turn);
            Assert.That(original, Is.Not.EqualTo(DefaultOf(property.PropertyType)),
                $"test data must populate AgentTranscriptTurn.{property.Name}");
            if (property.Name == nameof(AgentTranscriptTurn.ToolCalls))
            {
                continue;
            }

            Assert.That(property.GetValue(rewrittenTurn), Is.EqualTo(original), $"AgentTranscriptTurn.{property.Name} was not copied");
        }

        Assert.That(rewrittenTurn.ToolCalls, Has.Count.EqualTo(turn.ToolCalls.Count));
        Assert.That(rewrittenTurn.ToolCalls[1], Is.SameAs(turn.ToolCalls[1]));

        var changedByVariant = new[] { nameof(AgentToolCallRecord.ResultJson), nameof(AgentToolCallRecord.TrailingText) };
        foreach (var property in typeof(AgentToolCallRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var original = property.GetValue(originalCall);
            Assert.That(original, Is.Not.EqualTo(DefaultOf(property.PropertyType)),
                $"test data must populate AgentToolCallRecord.{property.Name}");
            var rewritten = property.GetValue(rewrittenCall);
            if (changedByVariant.Contains(property.Name))
            {
                Assert.That(rewritten, Is.Not.EqualTo(original), $"AgentToolCallRecord.{property.Name} should carry the variant");
            }
            else
            {
                Assert.That(rewritten, Is.EqualTo(original), $"AgentToolCallRecord.{property.Name} was not copied");
            }
        }
    }

    // ---- helpers ----

    private static AgentTranscript Load(string fileName) =>
        SavedTranscript.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

    private static ErrorVariant IdentityVariant(AgentToolCallRecord recorded)
    {
        Assert.That(ResultOverride.TryReadError(recorded.ResultJson, out var code, out var message), Is.True);
        return new ErrorVariant("identity", code, message, null, "000000000000");
    }

    private static ErrorVariant SyntheticVariant(string errorCode) =>
        new("synthetic", errorCode, "variant message", null, "000000000000");

    private static void AssertIdentical(AgentToolCallRecord actual, AgentToolCallRecord recorded)
    {
        Assert.That(actual.ResultJson, Is.EqualTo(recorded.ResultJson));
        Assert.That(Encoding.UTF8.GetBytes(actual.ResultJson), Is.EqualTo(Encoding.UTF8.GetBytes(recorded.ResultJson)));
        Assert.That(actual.TrailingText, Is.EqualTo(recorded.TrailingText));
        Assert.That(actual.ToolName, Is.EqualTo(recorded.ToolName));
        Assert.That(actual.ArgumentsJson, Is.EqualTo(recorded.ArgumentsJson));
        Assert.That(actual.IsError, Is.EqualTo(recorded.IsError));
        Assert.That(actual.ToolCallId, Is.EqualTo(recorded.ToolCallId));
    }

    private static string ErrorEnvelope(string errorCode, string rawMessage) =>
        "{\"isError\":true,\"errorData\":{\"errorCode\":\"" + errorCode + "\",\"message\":\"" + rawMessage + "\"},\"findings\":[]}";

    private static AgentTranscript BuildTranscript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript { RunId = "run-1" };
        transcript.Turns.AddRange(turns);
        return transcript;
    }

    private static AgentToolCallRecord MakeCall(string resultJson, bool isError) => new()
    {
        ToolName = "ReadFile",
        ArgumentsJson = "{\"filePath\":\"a.cs\"}",
        ResultJson = resultJson,
        IsError = isError,
        Latency = TimeSpan.FromMilliseconds(12),
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(1),
        ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
        TrailingText = "recorded trailing",
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

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
