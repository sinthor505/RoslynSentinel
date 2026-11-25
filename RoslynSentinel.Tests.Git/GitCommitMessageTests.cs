using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The rules of <see cref="GitCommitMessage.Format"/> that need no git process: cleanup, trailer placement for the shapes the commit tool produces, and the typed declines. The expected texts
/// are the ones <see cref="GitCommitMessageParityTests"/> proves equal to git's stored bytes for the same input.
/// </summary>
[TestFixture]
public class GitCommitMessageTests
{
    [TestCase("subject", "subject\n")]
    [TestCase("\n\n  subject  \n\n\n\nbody \t\n\n\n", "  subject\n\nbody\n")]
    [TestCase("a\r\nb\r\n", "a\nb\n")]
    [TestCase("# kept\nline", "# kept\nline\n")]
    [TestCase("tab\tinside\t", "tab\tinside\n")]
    public void Cleanup_MatchesWhitespaceMode(string message, string expected)
    {
        var result = GitCommitMessage.Format(message);

        Assert.That(result.Outcome, Is.EqualTo(CommitMessageOutcome.Formatted), result.Detail);
        Assert.That(result.Message, Is.EqualTo(expected));
    }

    [Test]
    public void SubjectOnly_GetsBlankLineThenTrailers()
    {
        var result = GitCommitMessage.Format("subject", "id1", "agent1");

        Assert.That(result.Message, Is.EqualTo("subject\n\nTool-Call-Id: id1\nTool-Agent: agent1\n"));
    }

    [Test]
    public void ExistingCoAuthoredByBlock_IsExtendedWithoutBlankLine()
    {
        var result = GitCommitMessage.Format("subject\n\nbody\n\nCo-Authored-By: A <a@b.c>", "id1");

        Assert.That(result.Message, Is.EqualTo("subject\n\nbody\n\nCo-Authored-By: A <a@b.c>\nTool-Call-Id: id1\n"));
    }

    [Test]
    public void ExistingToolCallId_IsReplacedAndMovedToTheEnd()
    {
        var result = GitCommitMessage.Format("subject\n\nTool-Call-Id: old\nReviewed-by: R", "new");

        Assert.That(result.Message, Is.EqualTo("subject\n\nReviewed-by: R\nTool-Call-Id: new\n"));
    }

    [Test]
    public void BlankAgent_AddsNoAgentTrailer()
    {
        var result = GitCommitMessage.Format("subject", "id1", "   ");

        Assert.That(result.Message, Is.EqualTo("subject\n\nTool-Call-Id: id1\n"));
    }

    [TestCase("a\0b")]
    [TestCase("")]
    [TestCase("   \n\t\n")]
    [TestCase("# \n")]
    public void Declines_WhatGitRejectsOrReadsDifferently(string message)
    {
        // "# \n" is kept by cleanup, so it formats; every other case declines.
        var result = GitCommitMessage.Format(message);

        Assert.That(result.IsFormatted, Is.EqualTo(message == "# \n"), result.Message);
        if (!result.IsFormatted)
        {
            Assert.That(result.Detail, Is.Not.Empty);
        }
    }

    [Test]
    public void LoneSurrogate_Declines()
    {
        // Built at run time: a lone surrogate in an attribute argument does not survive NUnit's metadata round trip.
        var result = GitCommitMessage.Format("lone " + '\ud800' + " surrogate");

        Assert.That(result.Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
        Assert.That(GitCommitMessage.Format("pair 😀 ok").IsFormatted, Is.True);
    }

    [TestCase("Signed-off-by: N <n@x.y>")]
    [TestCase("\nSigned-off-by: N <n@x.y>\n\nSigned-off-by: M <m@x.y>\n")]
    public void OnlySignOffLines_DeclineWithoutTrailers_FormatWithThem(string message)
    {
        // git aborts such a message ("empty commit message") unless a trailer line makes it non-empty.
        Assert.That(GitCommitMessage.Format(message).Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
        Assert.That(GitCommitMessage.Format(message, "id1").IsFormatted, Is.True);
    }

    [TestCase("   ")]
    [TestCase("")]
    [TestCase("two\nlines")]
    public void BlankOrMultiLineTrailerValue_Declines(string callId)
    {
        Assert.That(GitCommitMessage.Format("subject", callId).Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
    }

    [Test]
    public void MultiLineAgent_Declines()
    {
        Assert.That(GitCommitMessage.Format("subject", "id1", "a\nb").Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
    }

    [TestCase("subject\n\nConflicts:\n\tfile.txt\n")]
    [TestCase("subject\n\nbody\n# ------------------------ >8 ------------------------\ndropped")]
    public void ScissorsOrConflictsLine_DeclinesWithTrailers_ButNotWithout(string message)
    {
        Assert.That(GitCommitMessage.Format(message, "id1").Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
        Assert.That(GitCommitMessage.Format(message).IsFormatted, Is.True);
    }
}
