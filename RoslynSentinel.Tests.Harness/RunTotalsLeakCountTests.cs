using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="RunTotals.FromTranscript"/> counts the turns whose model message content still carries a close think tag
/// (a leaked reasoning block). Reasoning-only turns are not counted, since the tag there is expected.
/// </summary>
[TestFixture]
public class RunTotalsLeakCountTests
{
    [Test]
    public void FromTranscript_CountsTurnsWithCloseTagInContent()
    {
        var transcript = TranscriptWith(
            Assistant("plan" + ThinkTagText.CloseTag + "answer", null),
            Assistant(null, "thinking" + ThinkTagText.CloseTag),
            Assistant("answer", null));

        var totals = RunTotals.FromTranscript(transcript, TimeSpan.Zero);

        Assert.That(totals.LeakedThinkTagTurnCount, Is.EqualTo(1));
    }

    private static AgentChatMessage Assistant(string? content, string? reasoning) =>
        new() { Role = "assistant", Content = content, ReasoningContent = reasoning };

    private static AgentTranscript TranscriptWith(params AgentChatMessage[] messages)
    {
        var transcript = new AgentTranscript();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < messages.Length; i++)
        {
            transcript.Turns.Add(new AgentTranscriptTurn
            {
                TurnNumber = i + 1,
                ModelMessage = messages[i],
                ModelLatency = TimeSpan.Zero,
                StartedAt = now,
                CompletedAt = now,
            });
        }

        return transcript;
    }
}
