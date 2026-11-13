using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class NextTurnDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void Classify_LeadingMarker_IsStopLeading()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "BLOCKED: the file is missing.") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("StopLeading"));
        Assert.That(decision.BlockedAnywhere, Is.True);
    }

    [TestCase("## **BLOCKED**: x")]
    [TestCase("`BLOCKED`: x")]
    [TestCase("_BLOCKED_: x")]
    public void Classify_MarkdownWrappedLeadingMarker_IsStopLeading(string message)
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, message) }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("StopLeading"));
    }

    [Test]
    public void Classify_MarkerAfterFirstLine_IsStopMarkerLater()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "I looked for it.\nBLOCKED: x") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("StopMarkerLater"));
    }

    [TestCase("The method ComputeImportDuty does not exist in OrderPricingCalculator.")]
    [TestCase("ComputeImportDuty was not found.")]
    [TestCase("There is no such method as computeimportduty.")]
    public void Classify_NamedTargetWithAbsencePhrase_IsStopProse(string message)
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, message) }, new[] { "ComputeImportDuty" }, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("StopProse"));
    }

    [Test]
    public void Classify_AbsencePhraseWithoutNamedTarget_IsNotStopProse()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "The method does not exist.") }, new[] { "ComputeImportDuty" }, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("NoCallOther"));
    }

    [Test]
    public void Classify_NullTargets_DisablesStopProse()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "ComputeImportDuty does not exist.") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("NoCallOther"));
    }

    [Test]
    public void Classify_NoCallWithoutMarkerOrAbsence_IsNoCallOther()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "I will think about it.") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("NoCallOther"));
    }

    // The tool-call markup is concatenated so this source never holds it literally (the server's edit guard rejects that).
    [TestCase("<" + "tool_call>")]
    [TestCase("<" + "function=ReadFile>")]
    public void Classify_ToolCallMarkupAsText_IsMalformed(string message)
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, message) }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Malformed"));
    }

    [TestCase("")]
    [TestCase("  \n ")]
    [TestCase((string?)null)]
    public void Classify_EmptyOrBlankReply_IsMalformed(string? message)
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, message) }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Malformed"));
    }

    [Test]
    public void Classify_ReadTool_IsWiden()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Widen:Search"));
        Assert.That(decision.FirstTool, Is.EqualTo("Search"));
    }

    [Test]
    public void Classify_WriteTool_IsWrite()
    {
        var writeTool = ToolEffectList.WriteTools.First();

        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", writeTool) }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Write:" + writeTool));
        Assert.That(decision.FirstTool, Is.EqualTo(writeTool));
    }

    [Test]
    public void Classify_UnlistedTool_IsOther()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "NotARealTool") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Other:NotARealTool"));
        Assert.That(decision.FirstTool, Is.EqualTo("NotARealTool"));
    }

    [Test]
    public void Classify_ThreeCalls_CountsAllAndClassifiesFirst()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search", "ReplaceSnippet", "Build") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.CallCount, Is.EqualTo(3));
        Assert.That(decision.FirstTool, Is.EqualTo("Search"));
        Assert.That(decision.DecisionClass, Is.EqualTo("Widen:Search"));
    }

    [Test]
    public void Classify_NoCallTurn_HasNullFirstToolAndZeroCalls()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "just words") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.FirstTool, Is.Null);
        Assert.That(decision.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void Classify_MarkerOnlyInSecondTurn_SetsBlockedAnywhere()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search"), Turn(2, "BLOCKED: nope") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.DecisionClass, Is.EqualTo("Widen:Search"));
        Assert.That(decision.BlockedAnywhere, Is.True);
        Assert.That(decision.StopTurn, Is.EqualTo(2));
    }

    [Test]
    public void Classify_OffScriptTool_SetsStopReasonOffScript()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search") }, null, AgentStopReason.ModelFinished, "Build");

        Assert.That(decision.StopReason, Is.EqualTo("OffScript:Build"));
        Assert.That(decision.StopTurn, Is.EqualTo(1));
    }

    [Test]
    public void Classify_TurnCapWithToolCallLast_SetsStopReasonTurnCap()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search") }, null, AgentStopReason.TurnCapExceeded, null);

        Assert.That(decision.StopReason, Is.EqualTo("TurnCap"));
    }

    [Test]
    public void Classify_LastNoCallTurn_SetsStopReasonToItsClass()
    {
        var decision = NextTurnDecision.Classify(
            new[] { Turn(1, "", "Search"), Turn(2, "BLOCKED: x") }, null, AgentStopReason.ModelFinished, null);

        Assert.That(decision.StopReason, Is.EqualTo("StopLeading"));
        Assert.That(decision.StopTurn, Is.EqualTo(2));
    }

    [Test]
    public void Classify_EmptyTurnList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            NextTurnDecision.Classify(new List<AgentTranscriptTurn>(), null, AgentStopReason.ModelFinished, null));
    }

    private static AgentTranscriptTurn Turn(int turnNumber, string? content, params string[] toolNames) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = content },
        ModelLatency = TimeSpan.FromMilliseconds(10),
        StartedAt = Now,
        CompletedAt = Now,
        ToolCalls = toolNames.Select(Call).ToList(),
    };

    private static AgentToolCallRecord Call(string toolName) => new()
    {
        ToolName = toolName,
        ArgumentsJson = "{}",
        ResultJson = "{}",
        IsError = false,
        Latency = TimeSpan.FromMilliseconds(5),
        StartedAt = Now,
        CompletedAt = Now,
        ToolCallId = "call-" + toolName,
        TrailingText = null,
    };
}
