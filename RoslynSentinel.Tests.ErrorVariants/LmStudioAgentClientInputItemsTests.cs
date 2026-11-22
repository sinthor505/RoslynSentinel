// LmStudioAgentClient sends the whole history every turn (stateless Responses API). These tests pin
// that an assistant turn's reasoning and message text are replayed, not only its tool calls: without
// the reasoning item the model loses its own plan between turns.

using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests;

[TestFixture]
public class LmStudioAgentClientInputItemsTests
{
    [Test]
    public void ToolCallTurnWithReasoning_SendsReasoningItemBeforeFunctionCall()
    {
        var items = Serialize(new AgentChatMessage
        {
            Role = "assistant",
            ReasoningContent = "plan: step 7 next",
            ToolCalls = [new AgentToolCall { Id = "call_1", Name = "ReadFile", ArgumentsJson = "{}" }],
        });

        Assert.That(items.Select(Type), Is.EqualTo(new[] { "reasoning", "function_call" }));
        var part = items[0].GetProperty("content")[0];
        Assert.That(part.GetProperty("type").GetString(), Is.EqualTo("reasoning_text"));
        Assert.That(part.GetProperty("text").GetString(), Is.EqualTo("plan: step 7 next"));
        Assert.That(items[1].GetProperty("call_id").GetString(), Is.EqualTo("call_1"));
    }

    [Test]
    public void ToolCallTurnWithMessageText_SendsTheTextToo()
    {
        var items = Serialize(new AgentChatMessage
        {
            Role = "assistant",
            Content = "Reading the file first.",
            ToolCalls = [new AgentToolCall { Id = "call_1", Name = "ReadFile", ArgumentsJson = "{}" }],
        });

        Assert.That(items.Select(Type), Is.EqualTo(new[] { "message", "function_call" }));
        Assert.That(items[0].GetProperty("content").GetString(), Is.EqualTo("Reading the file first."));
    }

    [Test]
    public void FinalTurn_SendsReasoningThenMessage()
    {
        var items = Serialize(new AgentChatMessage { Role = "assistant", Content = "done", ReasoningContent = "checked" });

        Assert.That(items.Select(Type), Is.EqualTo(new[] { "reasoning", "message" }));
        Assert.That(items[1].GetProperty("role").GetString(), Is.EqualTo("assistant"));
    }

    [Test]
    public void ToolCallTurnWithoutReasoningOrText_SendsOnlyTheFunctionCall()
    {
        var items = Serialize(new AgentChatMessage
        {
            Role = "assistant",
            ToolCalls = [new AgentToolCall { Id = "call_1", Name = "ReadFile", ArgumentsJson = "{}" }],
        });

        Assert.That(items.Select(Type), Is.EqualTo(new[] { "function_call" }));
    }

    private static AgentChatMessage[] PlanTurns(int count) => Enumerable.Range(1, count)
        .Select(i => new AgentChatMessage
        {
            Role = "assistant",
            ReasoningContent = $"plan {i}",
            ToolCalls = [new AgentToolCall { Id = $"call_{i}", Name = "ReadFile", ArgumentsJson = "{}" }],
        })
        .ToArray();

    private List<string?> ReplayedReasoning(AgentChatMessage[] turns) => Serialize(turns).Where(e => Type(e) == "reasoning")
        .Select(e => e.GetProperty("content")[0].GetProperty("text").GetString())
        .ToList();

    [Test]
    public void ReasoningWindow_KeepsEveryTurn_UntilTwoBlocks()
    {
        var window = LmStudioAgentClient.ReasoningReplayTurns;

        var reasoning = ReplayedReasoning(PlanTurns(2 * window - 1));

        Assert.That(reasoning, Has.Count.EqualTo(2 * window - 1));
        Assert.That(reasoning[0], Is.EqualTo("plan 1"));
    }

    [Test]
    public void ReasoningWindow_DropsTheOldestTurnsOneBlockAtATime()
    {
        var window = LmStudioAgentClient.ReasoningReplayTurns;

        var reasoning = ReplayedReasoning(PlanTurns(2 * window + 3));

        Assert.That(reasoning, Has.Count.EqualTo(window + 3));
        Assert.That(reasoning[0], Is.EqualTo($"plan {window + 1}"));
        Assert.That(reasoning[^1], Is.EqualTo($"plan {2 * window + 3}"));
    }

    [Test]
    public void ReasoningWindow_KeepsTheEarlierRequestAsAPrefix_BetweenBlockSteps()
    {
        // The prompt cache only reuses an unchanged prefix: inside a block, request n must start with request n-1.
        var window = LmStudioAgentClient.ReasoningReplayTurns;
        var turns = PlanTurns(2 * window + 3);

        // Reasoning items get a fresh random id per request; the chat template LM Studio renders (and caches) omits it.
        static string WithoutId(JsonElement e) => System.Text.RegularExpressions.Regex.Replace(e.GetRawText(), "\"id\":\"rs_[0-9a-f]+\"", "\"id\":\"rs_\"");
        var earlier = Serialize(turns[..^1]).Select(WithoutId).ToList();
        var later = Serialize(turns).Select(WithoutId).ToList();

        Assert.That(later.Take(earlier.Count), Is.EqualTo(earlier));
    }

    [TestCase(0, 45, 0)]
    [TestCase(10, 19, 0)]
    [TestCase(10, 23, 10)]
    [TestCase(20, 45, 20)]
    public void ReasoningDroppedTurns_StepsByTheGivenWindow_AndZeroKeepsAll(int window, int turns, int expectedDropped)
    {
        Assert.That(LmStudioAgentClient.ReasoningDroppedTurns(turns, window), Is.EqualTo(expectedDropped));
    }

    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase(" 0 ", 0)]
    [TestCase("15", 15)]
    public void ParseReasoningWindowOverride_AcceptsUnsetOrNonNegative(string? text, int? expected)
    {
        Assert.That(LmStudioAgentClient.ParseReasoningWindowOverride(text), Is.EqualTo(expected));
    }

    [TestCase("-1")]
    [TestCase("all")]
    public void ParseReasoningWindowOverride_Throws_WhenNotANonNegativeInteger(string text)
    {
        var ex = Assert.Throws<FormatException>(() => LmStudioAgentClient.ParseReasoningWindowOverride(text));
        Assert.That(ex!.Message, Does.Contain(LmStudioAgentClient.ReasoningWindowVariable));
    }

    // InputMessageItem carries no "type" property on the wire; report it as "message".
    private static string Type(JsonElement item) =>
        item.TryGetProperty("type", out var t) ? t.GetString()! : "message";

    private static List<JsonElement> Serialize(params AgentChatMessage[] messages)
    {
        using var doc = JsonDocument.Parse(LmStudioAgentClient.SerializeInputItems(messages));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
