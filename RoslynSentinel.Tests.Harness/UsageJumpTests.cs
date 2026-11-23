using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// ModelAgentRunner.IsUsageJumpImplausible: a backend usage report that grew far more than the added content can
/// explain is ignored by the context warning and the context-exhausted check.
/// </summary>
[TestFixture]
public class UsageJumpTests
{
    [Test]
    public void ReportAboutTwiceThePrompt_IsImplausible()
    {
        // 10-chain batch Y run 4: T2 total 25821, T3 reported 53916 although only a ~1 KB CreateFile result was added.
        Assert.That(ModelAgentRunner.IsUsageJumpImplausible(25821, 53916, 300), Is.True);
    }

    [Test]
    public void OrdinaryGrowth_IsPlausible()
    {
        Assert.That(ModelAgentRunner.IsUsageJumpImplausible(30000, 34000, 2500), Is.False);
    }

    [Test]
    public void LargeToolResultCoveredByTheEstimate_IsPlausible()
    {
        Assert.That(ModelAgentRunner.IsUsageJumpImplausible(30000, 45000, 10000), Is.False);
    }

    [Test]
    public void ShrinkingTotal_IsPlausible()
    {
        // A reasoning-window block step shortens the prompt.
        Assert.That(ModelAgentRunner.IsUsageJumpImplausible(44463, 44188, 400), Is.False);
    }

    [Test]
    public void FirstReport_IsPlausible()
    {
        Assert.That(ModelAgentRunner.IsUsageJumpImplausible(0, 60000, 0), Is.False);
    }

    [Test]
    public void MessageEstimate_CountsToolCallArguments()
    {
        var empty = new AgentChatMessage { Role = "assistant" };
        var withCall = new AgentChatMessage
        {
            Role = "assistant",
            ToolCalls = [new AgentToolCall { Id = "c1", Name = "ReplaceSnippet", ArgumentsJson = new string('x', 4000) }],
        };

        Assert.That(ModelAgentRunner.EstimateMessageTokens(withCall),
            Is.GreaterThan(ModelAgentRunner.EstimateMessageTokens(empty) + 500));
    }
}
