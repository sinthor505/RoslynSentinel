// ReasoningReplayPolicy decides which assistant turns replay their reasoning on the next request. These tests
// pin pressure mode (drop the oldest half of the unpinned turns once the projected prompt reaches 70% of the
// context), the fixed block window used by an explicit window and by unknown context lengths, and the mode
// text recorded in the transcript. Policies are built with fixedWindow passed directly; no test sets
// ROSLYNSENTINEL_REASONING_WINDOW, which is process-wide.

using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ReasoningReplayPolicyTests
{
    [Test]
    public void Pressure_BelowThreshold_ReplaysEveryTurn()
    {
        var policy = new ReasoningReplayPolicy(1000, null);
        var turns = PlanTurns(30);

        Assert.That(policy.Observe(30, 699), Is.False);

        Assert.That(ReplayedReasoning(turns, policy), Has.Count.EqualTo(30));
    }

    [Test]
    public void Pressure_AtThreshold_DropsTheOldestHalfOfTheUnpinnedTurns()
    {
        var policy = new ReasoningReplayPolicy(1000, null);
        var turns = PlanTurns(30);

        Assert.That(policy.Observe(30, 700), Is.True);

        // 30 turns - 3 pinned = 27 replayable; half of those (13) are dropped.
        Assert.That(policy.DroppedTurns, Is.EqualTo(13));
        var expected = Enumerable.Range(1, 3).Concat(Enumerable.Range(17, 14)).Select(i => $"plan {i}").ToList();
        Assert.That(ReplayedReasoning(turns, policy), Is.EqualTo(expected));
    }

    [Test]
    public void Pressure_NeverDropsTheFirstThreeTurns()
    {
        var policy = new ReasoningReplayPolicy(1000, null);
        var turns = PlanTurns(30);

        // Keep observing at high pressure until the policy stops dropping; it must settle well before 20 drops.
        var drops = 0;
        while (policy.Observe(30, 900))
        {
            drops++;
            Assert.That(drops, Is.LessThan(20), "pressure drops should settle well before 20 observations");
        }

        Assert.That(drops, Is.GreaterThan(0));
        Assert.That(policy.DroppedTurns, Is.LessThanOrEqualTo(27));
        var reasoning = ReplayedReasoning(turns, policy);
        Assert.That(reasoning.Take(3), Is.EqualTo(new[] { "plan 1", "plan 2", "plan 3" }));
        Assert.That(reasoning[^1], Is.EqualTo("plan 30"));
        Assert.That(policy.Observe(30, 900), Is.False);
    }

    [Test]
    public void Pressure_KeepsTheEarlierRequestAsAPrefix_BetweenDrops()
    {
        var policy = new ReasoningReplayPolicy(1000, null);
        var turns = PlanTurns(30);
        Assert.That(policy.Observe(30, 700), Is.True);

        // Same policy instance, no Observe in between: the earlier request must stay a prefix of the later one.
        // Reasoning items get a fresh random id per request; the chat template LM Studio renders (and caches) omits it.
        static string WithoutId(JsonElement e) => System.Text.RegularExpressions.Regex.Replace(e.GetRawText(), "\"id\":\"rs_[0-9a-f]+\"", "\"id\":\"rs_\"");
        var earlier = Serialize(turns[..^1], policy).Select(WithoutId).ToList();
        var later = Serialize(turns, policy).Select(WithoutId).ToList();

        Assert.That(earlier, Is.Not.Empty);
        Assert.That(later.Take(earlier.Count), Is.EqualTo(earlier));
    }

    [Test]
    public void WindowZero_NeverDrops()
    {
        var policy = new ReasoningReplayPolicy(1000, 0);

        Assert.That(policy.Mode, Is.EqualTo("all"));
        Assert.That(policy.FixedWindow, Is.EqualTo(0));
        Assert.That(policy.Observe(45, 100000), Is.False);
        Assert.That(ReplayedReasoning(PlanTurns(45), policy), Has.Count.EqualTo(45));
    }

    [TestCase(10, 9)]
    [TestCase(10, 19)]
    [TestCase(10, 23)]
    [TestCase(10, 45)]
    [TestCase(20, 45)]
    [TestCase(20, 50)]
    public void WindowN_MatchesTheLegacyBlockWindow(int window, int turnCount)
    {
        var policy = new ReasoningReplayPolicy(1000, window);
        var dropped = LmStudioAgentClient.ReasoningDroppedTurns(turnCount, window);

        Assert.That(policy.Observe(turnCount, 1_000_000), Is.False);

        var reasoning = ReplayedReasoning(PlanTurns(turnCount), policy);
        Assert.That(reasoning, Has.Count.EqualTo(turnCount - dropped));
        Assert.That(reasoning[0], Is.EqualTo($"plan {dropped + 1}"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void UnknownContext_FallsBackToTheTenTurnBlockWindow(int contextLength)
    {
        var policy = new ReasoningReplayPolicy(contextLength, null);

        Assert.That(policy.FixedWindow, Is.EqualTo(10));
        Assert.That(policy.Mode, Is.EqualTo("window:10 (context length unknown)"));
        Assert.That(policy.Observe(45, 1_000_000), Is.False);
        Assert.That(policy.DroppedTurns, Is.EqualTo(0));
    }

    [Test]
    public void ExplicitWindowTen_WithUnknownContext_IsNotLabelledAsFallback()
    {
        var policy = new ReasoningReplayPolicy(0, 10);

        Assert.That(policy.Mode, Is.EqualTo("window:10"));
        Assert.That(policy.FixedWindow, Is.EqualTo(10));
    }

    [Test]
    public void Mode_NamesThePolicy()
    {
        Assert.That(new ReasoningReplayPolicy(1000, null).Mode, Is.EqualTo("pressure:0.7,pinned:3"));
        Assert.That(new ReasoningReplayPolicy(1000, 0).Mode, Is.EqualTo("all"));
        Assert.That(new ReasoningReplayPolicy(1000, 10).Mode, Is.EqualTo("window:10"));
    }

    private static AgentChatMessage[] PlanTurns(int count) => Enumerable.Range(1, count)
        .Select(i => new AgentChatMessage
        {
            Role = "assistant",
            ReasoningContent = $"plan {i}",
            ToolCalls = [new AgentToolCall { Id = $"call_{i}", Name = "ReadFile", ArgumentsJson = "{}" }],
        })
        .ToArray();

    private static List<string?> ReplayedReasoning(AgentChatMessage[] turns, ReasoningReplayPolicy policy) =>
        Serialize(turns, policy).Where(e => Type(e) == "reasoning")
            .Select(e => e.GetProperty("content")[0].GetProperty("text").GetString())
            .ToList();

    // InputMessageItem carries no "type" property on the wire; report it as "message".
    private static string Type(JsonElement item) =>
        item.TryGetProperty("type", out var t) ? t.GetString()! : "message";

    private static List<JsonElement> Serialize(AgentChatMessage[] messages, ReasoningReplayPolicy policy)
    {
        using var doc = JsonDocument.Parse(LmStudioAgentClient.SerializeInputItems(messages, policy));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
