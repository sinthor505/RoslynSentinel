using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Model-free checks of the DONE:/BLOCKED: token that starts a final message.</summary>
[TestFixture]
public class FinalOutcomeTokenTests
{
    [Test]
    public void Parse_LeadingDone()
    {
        Assert.That(FinalOutcomeToken.Parse("DONE: finished the change\nFiles: a.cs"), Is.EqualTo(FinalOutcome.Done));
    }

    [TestCase("## **BLOCKED:** reason")]
    [TestCase("`BLOCKED:` reason")]
    public void Parse_LeadingBlockedWithMarkdown(string message)
    {
        Assert.That(FinalOutcomeToken.Parse(message), Is.EqualTo(FinalOutcome.Blocked));
    }

    [Test]
    public void Parse_TokenOnLastLine_IsDone()
    {
        Assert.That(FinalOutcomeToken.Parse("I checked the tree.\nDONE: finished the change"), Is.EqualTo(FinalOutcome.Done));
    }

    [Test]
    public void Parse_BlockedOnLastLineAfterReport_IsBlocked()
    {
        Assert.That(FinalOutcomeToken.Parse("Tried RenameSymbol on Foo.\nIt failed: SymbolNotFound.\n\n**BLOCKED:** Foo does not exist\n"), Is.EqualTo(FinalOutcome.Blocked));
    }

    [TestCase((string?)null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_NullAndBlank_AreNone(string? message)
    {
        Assert.That(FinalOutcomeToken.Parse(message), Is.EqualTo(FinalOutcome.None));
    }

    [Test]
    public void Parse_BlockedInsideSentence_IsNone()
    {
        Assert.That(FinalOutcomeToken.Parse("I am BLOCKED: on the rename tool."), Is.EqualTo(FinalOutcome.None));
    }
}
