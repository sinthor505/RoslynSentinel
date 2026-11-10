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

    // InputMessageItem carries no "type" property on the wire; report it as "message".
    private static string Type(JsonElement item) =>
        item.TryGetProperty("type", out var t) ? t.GetString()! : "message";

    private static List<JsonElement> Serialize(params AgentChatMessage[] messages)
    {
        using var doc = JsonDocument.Parse(LmStudioAgentClient.SerializeInputItems(messages));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
