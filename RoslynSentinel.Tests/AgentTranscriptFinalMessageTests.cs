// AgentTranscript.FinalMessage: the answer a SubAgent or PlanStepRunner reports is the last turn's
// Content, falling back to its ReasoningContent when Content is blank. These tests pin that choice,
// so a model that puts its whole answer in reasoning is not reported as an empty success.

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests;

[TestFixture]
public class AgentTranscriptFinalMessageTests
{
    [Test]
    public void NoTurns_FinalMessageIsEmptyAndNotFromReasoning()
    {
        var transcript = new AgentTranscript();

        Assert.That(transcript.FinalMessage(), Is.EqualTo(""));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.False);
    }

    [Test]
    public void LastTurnWithContent_ReturnsContentNotReasoning()
    {
        var transcript = TranscriptWith(Assistant("answer", "thinking"));

        Assert.That(transcript.FinalMessage(), Is.EqualTo("answer"));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.False);
    }

    [Test]
    public void NullContent_FallsBackToReasoning()
    {
        var transcript = TranscriptWith(Assistant(null, "thinking"));

        Assert.That(transcript.FinalMessage(), Is.EqualTo("thinking"));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.True);
    }

    [Test]
    public void WhitespaceContent_FallsBackToReasoning()
    {
        var transcript = TranscriptWith(Assistant("  \n", "thinking"));

        Assert.That(transcript.FinalMessage(), Is.EqualTo("thinking"));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.True);
    }

    [Test]
    public void BothBlank_ReturnsEmptyAndNotFromReasoning()
    {
        var transcript = TranscriptWith(Assistant("  ", null));

        Assert.That(transcript.FinalMessage(), Is.EqualTo(""));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.False);
    }

    [Test]
    public void OnlyTheLastTurnCounts_EarlierContentIsIgnored()
    {
        var transcript = TranscriptWith(Assistant("earlier", null), Assistant("", null));

        Assert.That(transcript.FinalMessage(), Is.EqualTo(""));
        Assert.That(transcript.FinalMessageIsFromReasoning(), Is.False);
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
