// Loop assist (proposal_first_person_turn_injection.md, "Loop breaking"): detection of identical rejected calls,
// the collapse of repeats out of the replayed history, the fact-only injection text, the env-var switch and the
// optional transcript fields. No live model: the tracker and the input-item serializer are plain code.

using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests;

[TestFixture]
public class LoopAssistTrackerTests
{
    private const string Args = """{"filePath":"a.cs","oldContent":"x"}""";
    private const string RenameRejection =
        "ReplaceSnippet: not applied. Next step: use RenameSymbol(a, b) instead.\r\nEdit impact: more detail on the next line.";

    // ---- repeat detection ----

    [Test]
    public void Observe_SameCallRejectedTheSameWayTwice_SecondIsAnInjectionAssist()
    {
        var tracker = On();

        var first = tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", RenameRejection), isError: true);

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(second!.Kind, Is.EqualTo(AgentAssist.InjectionKind));
            Assert.That(second.ToolName, Is.EqualTo("ReplaceSnippet"));
            Assert.That(second.RepeatCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Observe_DifferentArguments_IsNotARepeat()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2", args: """{"filePath":"b.cs","oldContent":"x"}"""), Rejection("c2", RenameRejection), isError: true);

        Assert.That(second, Is.Null);
    }

    [Test]
    public void Observe_DifferentToolName_IsNotARepeat()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2", tool: "RenameSymbol"), Rejection("c2", RenameRejection), isError: true);

        Assert.That(second, Is.Null);
    }

    [Test]
    public void Observe_SameCallButDifferentErrorText_IsNotARepeat()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", "ReplaceSnippet: oldContent not found in the file."), isError: true);

        Assert.That(second, Is.Null);
    }

    [Test]
    public void Observe_TrivialWhitespaceDifferencesInTheErrorText_StillRepeat()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", "It failed.\r\nNext step: do X."), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", "It  failed.\nNext step: do X. "), isError: true);

        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public void Observe_ServerRepeatNoteOnTheSecondRejection_StillCountsAsARepeat()
    {
        // The server prepends "You already sent this exact edit ..." from the 2nd identical rejection on, so the
        // 1st and 2nd texts differ by that sentence alone.
        var tracker = On();
        var note = "You already sent this exact edit and it was rejected the same way; sending it again cannot succeed. Do what the next step below says instead. ";

        tracker.Observe(Call("c1"), Rejection("c1", "ReplaceSnippet: not applied. Next step: use RenameSymbol."), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", "ReplaceSnippet: not applied. " + note + "Next step: use RenameSymbol."), isError: true);

        Assert.That(second, Is.Not.Null);
        Assert.That(second!.RepeatCount, Is.EqualTo(2));
    }

    [Test]
    public void Observe_SuccessfulCall_NeverCounts()
    {
        var tracker = On();

        var first = tracker.Observe(Call("c1"), """{"isError":false}""", isError: false);
        var second = tracker.Observe(Call("c2"), """{"isError":false}""", isError: false);

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Null);
    }

    [Test]
    public void Observe_RepeatsSeparatedByOtherCalls_StillCount()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        tracker.Observe(Call("c2", tool: "LocateSymbol", args: "{}"), Rejection("c2", "no such symbol"), isError: true);
        var third = tracker.Observe(Call("c3"), Rejection("c3", RenameRejection), isError: true);

        Assert.That(third, Is.Not.Null);
        Assert.That(third!.RepeatCount, Is.EqualTo(2));
    }

    [Test]
    public void Observe_RejectedCallsDifferingOnlyInReason_CountAsARepeat()
    {
        var tracker = On();

        var first = tracker.Observe(Call("c1", args: """{"filePath":"a.cs","oldContent":"x","reason":"first wording of why"}"""), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2", args: """{"filePath":"a.cs","oldContent":"x","reason":"a reworded reason"}"""), Rejection("c2", RenameRejection), isError: true);

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.RepeatCount, Is.EqualTo(2));
    }

    [Test]
    public void Observe_BatchEditsCallAndFlatCallWithTheSameEdit_AreNotARepeat()
    {
        var tracker = On();

        var batch = tracker.Observe(Call("c1", args: """{"batchEdits":[{"filePath":"a.cs","oldContent":"x","newContent":"y"}]}"""), Rejection("c1", RenameRejection), isError: true);
        var flat = tracker.Observe(Call("c2", args: """{"filePath":"a.cs","oldContent":"x","newContent":"y"}"""), Rejection("c2", RenameRejection), isError: true);

        Assert.That(batch, Is.Null);
        Assert.That(flat, Is.Null);
    }

    [Test]
    public void Observe_RunR9ShapedSequence_InjectsOnlyAfterTheSecondIdenticalFlatRejection()
    {
        // 10-chain batch E run R9: T2 is a batchEdits rejection, T3 and T4 are the same flat call rejected identically.
        var tracker = On();
        var batchArgs = """{"batchEdits":[{"filePath":"a.cs","oldContent":"x","newContent":"y"}]}""";

        var t2 = tracker.Observe(Call("t2", args: batchArgs), "{\"isError\":true,\"errorData\":{\"message\":\"ReplaceSnippet batch: edit 1 not applied.\"}}", isError: true);
        var t3 = tracker.Observe(Call("t3"), Rejection("t3", RenameRejection), isError: true);
        var t4 = tracker.Observe(Call("t4"), Rejection("t4", RenameRejection), isError: true);

        Assert.Multiple(() =>
        {
            Assert.That(t2, Is.Null);
            Assert.That(t3, Is.Null);
            Assert.That(t4, Is.Not.Null);
            Assert.That(t4!.RepeatCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Observe_NextStepQuotingThePlaceholderReason_DropsThatArgumentFromTheInjection()
    {
        var tracker = On();
        var error = """ReplaceSnippet: not applied. Next step: use RenameSymbol(reason: "<why you call this>", projectName: P) instead.""";

        tracker.Observe(Call("c1"), Rejection("c1", error), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", error), isError: true);

        Assert.That(second!.Text, Is.EqualTo(
            "I have sent the same ReplaceSnippet call 2 times and it was rejected the same way each time. " +
            "The error's next step says: use RenameSymbol(projectName: P) instead. My next call must be a different tool."));
        Assert.That(second.Text, Does.Not.Contain("<why you call this>"));
    }

    // ---- injection text ----

    [Test]
    public void Observe_SecondRejectionWithNextStep_QuotesTheNextStepLineVerbatim()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", RenameRejection), isError: true);

        Assert.That(second!.Text, Is.EqualTo(
            "I have sent the same ReplaceSnippet call 2 times and it was rejected the same way each time. " +
            "The error's next step says: use RenameSymbol(a, b) instead. My next call must be a different tool."));
    }

    [Test]
    public void Observe_SecondRejectionWithoutNextStep_OmitsTheMiddleSentence()
    {
        var tracker = On();

        tracker.Observe(Call("c1"), Rejection("c1", "ReplaceSnippet: oldContent not found."), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", "ReplaceSnippet: oldContent not found."), isError: true);

        Assert.That(second!.Text, Is.EqualTo(
            "I have sent the same ReplaceSnippet call 2 times and it was rejected the same way each time. " +
            "My next call must be a different tool."));
    }

    [Test]
    public void BuildInjectionText_NextStepWithoutTerminalPunctuation_GetsOnePeriod()
    {
        var text = LoopAssistTracker.BuildInjectionText("Search", 2, "call RenameSymbol");

        Assert.That(text, Does.Contain("The error's next step says: call RenameSymbol. My next call"));
    }

    [Test]
    public void ExtractNextStep_StopsAtTheEndOfTheLine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LoopAssistTracker.ExtractNextStep("a. Next step: do X. Then Y.\r\nEdit impact: z"), Is.EqualTo("do X. Then Y."));
            Assert.That(LoopAssistTracker.ExtractNextStep("Next step: last line only"), Is.EqualTo("last line only"));
            Assert.That(LoopAssistTracker.ExtractNextStep("no marker here"), Is.Null);
            Assert.That(LoopAssistTracker.ExtractNextStep("Next step: \nempty"), Is.Null);
        });
    }

    [Test]
    public void Observe_LaterRepeats_EscalateTheWordingAndNeverRepeatTheSameSentenceTwiceInARow()
    {
        var tracker = On();
        var texts = new List<string>();

        for (var i = 1; i <= 6; i++)
        {
            var assist = tracker.Observe(Call("c" + i), Rejection("c" + i, RenameRejection), isError: true);
            if (assist is not null)
            {
                texts.Add(assist.Text);
            }
        }

        Assert.That(texts, Has.Count.EqualTo(5));
        Assert.That(texts[1], Does.Contain("3rd time").And.Contain("The error's next step says: use RenameSymbol(a, b) instead."));
        Assert.That(texts[2], Does.Contain("4th time"));
        Assert.That(texts[3], Does.Contain("5th time"));
        for (var i = 1; i < texts.Count; i++)
        {
            Assert.That(texts[i], Is.Not.EqualTo(texts[i - 1]));
        }

        // Alternating templates, not just a changing number: the sentence frame differs between consecutive repeats.
        Assert.That(texts[1].Replace("3rd", "#"), Is.Not.EqualTo(texts[2].Replace("4th", "#")));
        Assert.That(texts[2].Replace("4th", "#"), Is.Not.EqualTo(texts[3].Replace("5th", "#")));
    }

    [TestCase(11, "11th")]
    [TestCase(12, "12th")]
    [TestCase(13, "13th")]
    [TestCase(21, "21st")]
    [TestCase(22, "22nd")]
    [TestCase(23, "23rd")]
    public void BuildInjectionText_UsesEnglishOrdinals(int count, string ordinal) =>
        Assert.That(LoopAssistTracker.BuildInjectionText("Search", count, null), Does.Contain(ordinal));

    // ---- collapse ----

    [Test]
    public void CollapseForReplay_KeepsTheFirstCallAndReplacesLaterPairsWithOneNote()
    {
        var (tracker, messages) = ThreeIdenticalRejections();

        var replay = tracker.CollapseForReplay(messages, []);
        var items = Items(replay);

        Assert.Multiple(() =>
        {
            Assert.That(items.Count(i => Type(i) == "function_call"), Is.EqualTo(1));
            Assert.That(items.Count(i => Type(i) == "function_call_output"), Is.EqualTo(1));
            Assert.That(items.Single(i => Type(i) == "function_call").GetProperty("call_id").GetString(), Is.EqualTo("c1"));
            var notes = items.Where(i => Type(i) == "message" && Role(i) == "user").ToList();
            Assert.That(notes, Has.Count.EqualTo(1));
            Assert.That(notes[0].GetProperty("content").GetString(), Is.EqualTo("(sent 2 more times, rejected identically)"));
            // Each turn's reasoning is still replayed, and no empty assistant message is left behind.
            Assert.That(items.Count(i => Type(i) == "reasoning"), Is.EqualTo(3));
            Assert.That(items.Any(i => Type(i) == "message" && Role(i) == "assistant"), Is.False);
        });
    }

    [Test]
    public void CollapseForReplay_ARunOfOneRepeat_UsesTheSingularNote()
    {
        var (tracker, messages) = IdenticalRejections(2);

        var items = Items(tracker.CollapseForReplay(messages, []));

        Assert.That(items.Single(i => Type(i) == "message" && Role(i) == "user").GetProperty("content").GetString(),
            Is.EqualTo("(sent 1 more time, rejected identically)"));
    }

    [Test]
    public void CollapseForReplay_LeavesTheRawMessageListUnchanged()
    {
        var (tracker, messages) = ThreeIdenticalRejections();
        var before = messages.ToList();

        tracker.CollapseForReplay(messages, []);

        Assert.That(messages, Is.EqualTo(before), "same messages, same order (reference equality)");
        Assert.That(messages.Sum(m => m.ToolCalls.Count), Is.EqualTo(3));
        Assert.That(Items(messages).Count(i => Type(i) == "function_call"), Is.EqualTo(3));
    }

    [Test]
    public void CollapseForReplay_ReportsACollapseAssistOnlyWhenAGroupGrew()
    {
        var tracker = On();
        var messages = new List<AgentChatMessage>();
        AddRejectedTurn(tracker, messages, 1);
        AddRejectedTurn(tracker, messages, 2);

        var firstAssists = new List<AgentAssist>();
        tracker.CollapseForReplay(messages, firstAssists);
        var unchangedAssists = new List<AgentAssist>();
        tracker.CollapseForReplay(messages, unchangedAssists);
        AddRejectedTurn(tracker, messages, 3);
        var grownAssists = new List<AgentAssist>();
        tracker.CollapseForReplay(messages, grownAssists);

        Assert.Multiple(() =>
        {
            Assert.That(firstAssists.Select(a => (a.Kind, a.RepeatCount)), Is.EqualTo(new[] { (AgentAssist.CollapseKind, 1) }));
            Assert.That(unchangedAssists, Is.Empty);
            Assert.That(grownAssists.Select(a => (a.Kind, a.RepeatCount)), Is.EqualTo(new[] { (AgentAssist.CollapseKind, 2) }));
            Assert.That(grownAssists[0].ToolName, Is.EqualTo("ReplaceSnippet"));
            Assert.That(grownAssists[0].Text, Is.EqualTo("(sent 2 more times, rejected identically)"));
        });
    }

    [Test]
    public void CollapseForReplay_NoRepeats_ReturnsTheSameListWithoutAssists()
    {
        var tracker = On();
        var messages = new List<AgentChatMessage>();
        AddRejectedTurn(tracker, messages, 1);
        var assists = new List<AgentAssist>();

        var replay = tracker.CollapseForReplay(messages, assists);

        Assert.That(replay, Is.SameAs(messages));
        Assert.That(assists, Is.Empty);
    }

    [Test]
    public void CollapseForReplay_AssistRoleNotesDoNotShiftTheReasoningReplayCap()
    {
        var tracker = new LoopAssistTracker(new LoopAssistOptions(true, "assistant"));
        var messages = new List<AgentChatMessage>();
        var turns = LmStudioAgentClient.ReasoningReplayTurns + 3;
        for (var i = 1; i <= turns; i++)
        {
            AddRejectedTurn(tracker, messages, i);
            if (tracker.Observe(Call("x"), "{}", isError: false) is not null)
            {
                Assert.Fail("a successful call must not assist");
            }
        }

        // An injection in the assistant role sits between turns; it must not count as a model turn.
        messages.Add(tracker.CreateAssistMessage("I have sent the same ReplaceSnippet call 2 times."));
        var items = Items(tracker.CollapseForReplay(messages, []));

        Assert.That(items.Count(i => Type(i) == "reasoning"), Is.EqualTo(LmStudioAgentClient.ReasoningReplayTurns));
        Assert.That(items.Count(i => Type(i) == "message" && Role(i) == "assistant"), Is.EqualTo(2), "the collapse note and the injection");
    }

    // ---- off, and options ----

    [Test]
    public void Disabled_ObserveAndCollapseChangeNothing()
    {
        var tracker = new LoopAssistTracker(LoopAssistOptions.Off);
        var messages = new List<AgentChatMessage>();

        var first = tracker.Observe(Call("c1"), Rejection("c1", RenameRejection), isError: true);
        var second = tracker.Observe(Call("c2"), Rejection("c2", RenameRejection), isError: true);
        var replay = tracker.CollapseForReplay(messages, []);

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Null);
        Assert.That(replay, Is.SameAs(messages));
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("0", false)]
    [TestCase("false", false)]
    [TestCase("1", true)]
    [TestCase("true", true)]
    [TestCase("TRUE", true)]
    [TestCase("on", true)]
    public void FromEnvironment_OnlyATruthyValueTurnsItOn(string? value, bool expected)
    {
        var options = LoopAssistOptions.FromEnvironment(name => name == LoopAssistOptions.EnabledVariable ? value : null);

        Assert.That(options.Enabled, Is.EqualTo(expected));
        Assert.That(options.InjectionRole, Is.EqualTo("user"));
    }

    [TestCase(null, "user")]
    [TestCase("user", "user")]
    [TestCase("assistant", "assistant")]
    [TestCase("Assistant", "assistant")]
    [TestCase("system", "user")]
    public void FromEnvironment_RoleDefaultsToUserAndAcceptsAssistant(string? value, string expected)
    {
        var options = LoopAssistOptions.FromEnvironment(name => name switch
        {
            LoopAssistOptions.EnabledVariable => "1",
            LoopAssistOptions.RoleVariable => value,
            _ => null,
        });

        Assert.That(options.InjectionRole, Is.EqualTo(expected));
    }

    [Test]
    public void EnvironmentVariableNames_AreTheDocumentedOnes()
    {
        Assert.That(LoopAssistOptions.EnabledVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST"));
        Assert.That(LoopAssistOptions.RoleVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST_ROLE"));
    }

    // ---- transcript serialization ----

    [Test]
    public void Transcript_WithoutAssists_OmitsAssistsAndAssistCount()
    {
        var transcript = new AgentTranscript { RunId = "r1" };
        transcript.Turns.Add(NewTurn());

        using var doc = JsonDocument.Parse(Serialize(transcript));

        Assert.That(doc.RootElement.TryGetProperty("AssistCount", out _), Is.False);
        Assert.That(doc.RootElement.GetProperty("Turns")[0].TryGetProperty("Assists", out _), Is.False);
    }

    [Test]
    public void Transcript_WithAssists_CarriesThemOnTheTurnAndCountsThemInTheHeader()
    {
        var turn = NewTurn();
        turn.Assists = [new AgentAssist(AgentAssist.InjectionKind, "ReplaceSnippet", 2, "I have sent the same ReplaceSnippet call 2 times.")];
        var transcript = new AgentTranscript { RunId = "r1", AssistCount = 1 };
        transcript.Turns.Add(turn);

        using var doc = JsonDocument.Parse(Serialize(transcript));

        var assist = doc.RootElement.GetProperty("Turns")[0].GetProperty("Assists")[0];
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("AssistCount").GetInt32(), Is.EqualTo(1));
            Assert.That(assist.GetProperty("Kind").GetString(), Is.EqualTo("injection"));
            Assert.That(assist.GetProperty("ToolName").GetString(), Is.EqualTo("ReplaceSnippet"));
            Assert.That(assist.GetProperty("RepeatCount").GetInt32(), Is.EqualTo(2));
            Assert.That(assist.GetProperty("Text").GetString(), Does.StartWith("I have sent the same"));
        });
    }

    [Test]
    public void Transcript_ZeroAssistCount_IsWrittenWhenLoopAssistWasOn()
    {
        using var doc = JsonDocument.Parse(Serialize(new AgentTranscript { RunId = "r1", AssistCount = 0 }));

        Assert.That(doc.RootElement.GetProperty("AssistCount").GetInt32(), Is.EqualTo(0));
    }

    [TestCase(true, 0, "Run passed clean.")]
    [TestCase(true, 1, "Run passed with 1 assist.")]
    [TestCase(true, 3, "Run passed with 3 assists.")]
    [TestCase(false, 0, "Run failed clean.")]
    [TestCase(false, 2, "Run failed with 2 assists.")]
    public void DescribeRunOutcome_SaysCleanOrTheAssistCount(bool converged, int assists, string expected) =>
        Assert.That(LoopAssistTracker.DescribeRunOutcome(converged, assists), Is.EqualTo(expected));

    // ---- helpers ----

    private static LoopAssistTracker On() => new(new LoopAssistOptions(true, "user"));

    private static AgentToolCall Call(string id, string tool = "ReplaceSnippet", string args = Args) =>
        new() { Id = id, Name = tool, ArgumentsJson = args };

    /// <summary>A rejected-call result shaped like the server's envelope, including its per-call toolCallId.</summary>
    private static string Rejection(string callId, string message) =>
        JsonSerializer.Serialize(new
        {
            toolCall = new { toolCallId = callId },
            isError = true,
            errorData = new { errorCode = "ValidationFailed", message },
        });

    private static (LoopAssistTracker Tracker, List<AgentChatMessage> Messages) ThreeIdenticalRejections() => IdenticalRejections(3);

    private static (LoopAssistTracker Tracker, List<AgentChatMessage> Messages) IdenticalRejections(int count)
    {
        var tracker = On();
        var messages = new List<AgentChatMessage>();
        for (var i = 1; i <= count; i++)
        {
            AddRejectedTurn(tracker, messages, i);
        }

        return (tracker, messages);
    }

    private static void AddRejectedTurn(LoopAssistTracker tracker, List<AgentChatMessage> messages, int index)
    {
        var call = Call("c" + index);
        var result = Rejection(call.Id, RenameRejection);
        messages.Add(new AgentChatMessage { Role = "assistant", ReasoningContent = $"plan {index}", ToolCalls = [call] });
        messages.Add(new AgentChatMessage { Role = "tool", ToolCallId = call.Id, Content = result });
        tracker.Observe(call, result, isError: true);
    }

    private static AgentTranscriptTurn NewTurn() => new()
    {
        TurnNumber = 1,
        ModelMessage = new AgentChatMessage { Role = "assistant" },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
    };

    // The same options ModelAgentRunner.WriteTranscriptAsync uses.
    private static string Serialize(AgentTranscript transcript) =>
        JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true });

    private static List<JsonElement> Items(IEnumerable<AgentChatMessage> messages)
    {
        using var doc = JsonDocument.Parse(LmStudioAgentClient.SerializeInputItems(messages));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // InputMessageItem carries no "type" property on the wire; report it as "message".
    private static string Type(JsonElement item) =>
        item.TryGetProperty("type", out var t) ? t.GetString()! : "message";

    private static string Role(JsonElement item) => item.GetProperty("role").GetString()!;
}
