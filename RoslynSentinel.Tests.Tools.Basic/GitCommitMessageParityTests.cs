using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;
using static RoslynSentinel.Tests.Tools.Basic.GitCommitMessageTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Byte parity of <see cref="GitCommitMessage.Format"/> with the installed git (step 5c-3 of proposal_libgit2sharp_in_process_git.md): every case commits with the real
/// <c>git commit -m ... --trailer ...</c> (the argument shape of <c>GitImpl.CommitAsync</c>) and compares the stored message bytes with the port, for three trailer variants
/// (none = cleanup only, call id, call id and agent). A decline is never a failure of the port in general but is a failure here: every case below is one the port must reproduce.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitMessageParityTests
{
    private string _repository = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!GitTestRepoAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repository = NewRepository("rs-commitmsg-");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_repository is not null)
        {
            GitHookTestSupport.DeleteTreeWithRetries(_repository);
        }
    }

    private static bool GitTestRepoAvailable() => RoslynSentinel.Tests.Battery.Basic.GitTestRepo.IsGitAvailable();

    private static readonly (string? CallId, string? Agent)[] TrailerVariants =
    [
        (null, null),
        ("0123456789ab", null),
        ("0123456789ab", "claude-sonnet"),
    ];

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    public static IEnumerable<TestCaseData> Cases()
    {
        TestCaseData Case(string name, string message) => new TestCaseData(message).SetName("Parity_" + name);

        yield return Case("SubjectOnly", "subject");
        yield return Case("SubjectOnlyNewline", "subject\n");
        yield return Case("SubjectAndBody", "subject\n\nbody text");
        yield return Case("MultiParagraph", "subject\n\nfirst paragraph\nsecond line\n\nsecond paragraph\n\nthird paragraph");
        yield return Case("ExistingCoAuthoredByBlock", "subject\n\nbody\n\nCo-Authored-By: A <a@b.c>");
        yield return Case("ExistingCoAuthoredByTwoLines", "subject\n\nbody\n\nCo-Authored-By: A <a@b.c>\nCo-Authored-By: B <b@b.c>\n");
        yield return Case("CoAuthoredByDirectlyAfterBody", "subject\n\nbody\nCo-Authored-By: A <a@b.c>");
        yield return Case("TrailerBlockPrecededByTextInSameParagraph", "subject\n\nSome prose.\nKey: value\nOther: thing");
        yield return Case("TrailerBlockWithProseAndSignedOffBy", "subject\n\nprose line\nSigned-off-by: A <a@b.c>\nmore prose");
        yield return Case("CherryPickedPrefix", "subject\n\nbody\n\n(cherry picked from commit abc123)");
        yield return Case("TrailingSpacesAndTabs", "subject   \n\nbody \t \nsecond  \t\n\n\ttail\t");
        yield return Case("CrlfLineEndings", "subject\r\n\r\nbody line\r\nsecond\r\n");
        yield return Case("CrlfWithTrailerBlock", "subject\r\n\r\nbody\r\n\r\nCo-Authored-By: A <a@b.c>\r\n");
        yield return Case("CarriageReturnInsideLine", "sub\rject\n\nbo\rdy\r");
        yield return Case("LeadingBlankLines", "\n\n\nsubject\n\nbody");
        yield return Case("LeadingWhitespaceOnlyLines", "  \n\t\nsubject\n\nbody");
        yield return Case("EmptyishSingleChar", "x");
        yield return Case("WhitespaceOnly", "   \n\t \n");
        yield return Case("OnlyNewlines", "\n\n\n");
        yield return Case("EndsInManyNewlines", "subject\n\nbody\n\n\n\n");
        yield return Case("EndsInWhitespaceLine", "subject\n\nbody\n   \n");
        yield return Case("NoFinalNewline", "subject\n\nbody without final newline");
        yield return Case("CommentLinesInBody", "subject\n\n# a comment\nbody\n#another\n\nmore");
        yield return Case("TrailingCommentLines", "subject\n\nbody\n\n# trailing comment\n# second");
        yield return Case("TrailingCommentAfterTrailerBlock", "subject\n\nbody\n\nCo-Authored-By: A <a@b.c>\n# c");
        yield return Case("OnlyCommentLines", "# one\n# two\n");
        yield return Case("LeadingCommentThenBody", "# first\nsubject\n\nbody");
        yield return Case("CommentLineAsTitleParagraph", "# note\n\nbody\n\nKey: v");
        yield return Case("CommentInsideTrailerBlock", "subject\n\nKey: a\n# inner\nKey2: b");
        yield return Case("IndentedLineAfterCommentInBlock", "subject\n\n# note\n  Tool-Call-Id: indented\nTool-: x\nTool: prefix");
        yield return Case("ContinuationAfterCommentInBlock", "subject\n\nKey: a\n# inner\n continued\nKey2: b");
        yield return Case("ExistingIdenticalToolCallId", "subject\n\nTool-Call-Id: 0123456789ab");
        yield return Case("ExistingDifferentToolCallId", "subject\n\nbody\n\nTool-Call-Id: old-id\nCo-Authored-By: A <a@b.c>");
        yield return Case("ExistingDifferentToolCallIdAndAgent", "subject\n\nTool-Call-Id: old-id\nTool-Agent: old-agent\nReviewed-by: R");
        yield return Case("ExistingToolAgentOnly", "subject\n\nbody\n\nTool-Agent: old-agent");
        yield return Case("ExistingTwoToolCallIds", "subject\n\nTool-Call-Id: one\nTool-Call-Id: two\nKey: v");
        yield return Case("ExistingLowerCaseToolCallId", "subject\n\ntool-call-id: lower\nKey: v");
        yield return Case("ToolCallIdAmongProse", "subject\n\nprose one\nprose two\nprose three\nprose four\nTool-Call-Id: old");
        yield return Case("TokenPrefixOfConfiguredName", "subject\n\nTool: short\nKey: v");
        yield return Case("SingleLetterToken", "subject\n\nT: one\nKey: v");
        yield return Case("NormalisedExistingTrailers", "subject\n\nKey:value\nKey2 :   v2  \nK3:\nK4 : \n");
        yield return Case("ContinuationLines", "subject\n\nKey: first\n second\n\tthird\nOther: x");
        yield return Case("IndentedFirstLineOfBlock", "subject\n\n  indented: x\nKey: v");
        yield return Case("DividerLineInMessage", "subject\n\nbody\n---\nKey: v");
        yield return Case("DividerBetweenBodyAndTrailers", "subject\n\nbody\n\n---\n\nKey: v");
        yield return Case("VeryLongLine", "subject\n\n" + Repeat("long line content ", 1100));
        yield return Case("VeryLongTrailerValue", "subject\n\nKey: " + Repeat("v", 5000));
        yield return Case("Unicode", "Zusammenfassung \u00fcber \u00e4nderungen\n\n\u65e5\u672c\u8a9e \u2713 caf\u00e9 \ud83d\ude00\n\nCo-Authored-By: J\u00fcrgen <j@x.de>");
        yield return Case("UnicodeNoBreakSpaceStaysContent", "subject\u00a0\n\nbody\u00a0\u00a0");
        yield return Case("VerticalTabAndFormFeed", "subject\u000b\n\nbody\u000c\n\u000c\n\nlast");
        yield return Case("QuotesAndBackslashes", "subject \"quoted\" and 'single'\n\npath C:\\dir\\ and trailing backslash \\");
        yield return Case("NonTrailerColonLines", "subject\n\nSee: this\nIt is: broken\nnote that a:b c");
        yield return Case("FixesHashReference", "subject\n\nFixes #12\nFixes: #13");
        yield return Case("TwoWordTokenIsNotATrailer", "subject\n\nTwo Words: not a trailer\nKey: v");
        yield return Case("TrailerBlockThenBlankThenProse", "subject\n\nKey: v\n\nclosing prose");
        yield return Case("FourTrailersThenProseUnrecognised", "subject\n\nprose\nA: 1\nB: 2\nC: 3\nD: 4");
        yield return Case("SignedOffByWithTwentyFivePercent", "subject\n\nprose\nprose\nprose\nSigned-off-by: A <a@b.c>");
        yield return Case("SignedOffByBelowTwentyFivePercent", "subject\n\nprose\nprose\nprose\nprose\nSigned-off-by: A <a@b.c>");
        yield return Case("BlankSeparatedTitleOnlyThenTrailers", "subject\n\nKey: v");
        yield return Case("HashAtStartOfMessage", "#hash first\n\nbody");
        yield return Case("PlainSingleLineBodyWithColon", "Fix: handle null\n\nDetails here");
        yield return Case("OnlySignedOffBy", "Signed-off-by: N <n@x.y>");
        yield return Case("OnlySignedOffByLinesAndBlanks", "\nSigned-off-by: N <n@x.y>\n\nSigned-off-by: M <m@x.y>\n");
        yield return Case("SignedOffByWithoutSpaceIsContent", "Signed-off-by:N <n@x.y>");
    }

    [TestCaseSource(nameof(Cases))]
    public void FormatMatchesGit_ForEveryTrailerVariant(string message)
    {
        var problems = new List<string>();
        foreach (var (callId, agent) in TrailerVariants)
        {
            var (status, detail) = CompareWithGit(_repository, message, callId, agent);
            if (status != "match")
            {
                problems.Add($"{status}: {detail}");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ValueShapes_MatchGit()
    {
        // Values git trims or takes verbatim: padded agent, a colon in the value, the call id of a Guid-sized length.
        var problems = new List<string>();
        foreach (var (message, callId, agent) in new (string, string?, string?)[]
                 {
                     ("subject\n\nbody", "0123456789ab", "  padded agent  "),
                     ("subject\n\nbody", "0123456789ab", "agent: with: colons"),
                     ("subject\n\nbody", "0123456789ab", "agent#hash"),
                     ("subject\n\nbody", "  spaced id  ", null),
                     ("subject\n\nbody", "id\twith\ttabs", "agent"),
                     ("subject\n\nbody", "a1b2c3d4e5f67890a1b2c3d4e5f67890", "\u00fcnicode-agent"),
                     ("subject", "0123456789ab", "   "),
                 })
        {
            var (status, detail) = CompareWithGit(_repository, message, callId, agent);
            if (status != "match")
            {
                problems.Add($"{status}: {detail}");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    private static readonly string[] FuzzLinePool =
    [
        "subject line", "subject line", "", "", "", "", "body text here", "more body words", "Co-Authored-By: A <a@b.c>", "Co-Authored-By: B <b@b.c>", "Key: value", "Key:value",
        "Key :  spaced  ", "Empty:", "Tool-Call-Id: old-1", "tool-call-id: lower", "Tool-Agent: old-agent", "Tool: prefix", "T: one", "Signed-off-by: N <n@x.y>",
        "(cherry picked from commit abc123)", "# comment", "#", " indented continuation", "\tTab continuation", "---", "Fixes #12", "a:b c", "see http://x/y", "-: dash",
        "\u00dcn\u00efcode: \u2713 \u65e5\u672c\u8a9e", "trailing spaces   ", "tab\t", "Two Words: not trailer", "\u000cformfeed: x", "\u000bvt", "  ", "Reviewed-by: R", "Tool-Call-Id:", "Tool-: x",
        "Co-authored-by:   X  ", "Tool-Agent:", "  Tool-Call-Id: indented", "A-B-C: x", "key with space : x", ":", "::", "x:", "Signed-off-by:", "(cherry picked from commit",
    ];

    private static readonly string[] FuzzTails = ["", "", "\n", "\n", "\n\n", "\n  \n", "\n# end comment", "\n\n# end comment\n", "\r\n"];

    private static readonly string?[] FuzzAgents = ["claude-1", "agent: a:b", "  padded  ", "Tool-X", null, null];

    [TestCase(20261111, 220)]
    [TestCase(424242, 160)]
    public void RandomizedMessages_MatchGit(int seed, int cases)
    {
        // Deterministic differential fuzz (fixed seeds): messages built from a pool of lines that exercise every branch of trailer.c's block detection.
        var random = new Random(seed);
        var problems = new List<string>();
        var declined = 0;
        for (var n = 0; n < cases; n++)
        {
            var lines = new List<string>();
            if (random.Next(5) == 0)
            {
                lines.AddRange(Enumerable.Repeat(random.Next(2) == 0 ? "" : "  ", random.Next(1, 3)));
            }

            for (var i = random.Next(1, 9); i > 0; i--)
            {
                lines.Add(FuzzLinePool[random.Next(FuzzLinePool.Length)]);
            }

            var message = string.Join(random.Next(7) == 0 ? "\r\n" : "\n", lines) + FuzzTails[random.Next(FuzzTails.Length)];
            string? callId = random.Next(4) == 0 ? null : "0123456789ab";
            var agent = callId is null ? null : FuzzAgents[random.Next(FuzzAgents.Length)];

            var (status, detail) = CompareWithGit(_repository, message, callId, agent);
            if (status == "declined")
            {
                declined++;
            }

            if (status != "match")
            {
                problems.Add($"#{n} {status}: {detail}");
            }
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} of {cases} differ ({declined} declined):{Environment.NewLine}" + string.Join(Environment.NewLine, problems));
    }
}
