using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for <see cref="SnippetLookup"/> (slice 4b of the whitespace-tolerance proposal): the lookup policy is
/// IgnoreAllWhitespace plus case-insensitive, a failure is never turned into a best-guess match, and the throwing
/// and Try forms report failures through <see cref="SnippetErrorFormatter"/> so the candidates are listed.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetLookupTests
{
    private static readonly string[] TypoFile =
    {
        "    var total = Compute(a, b);",
        "    return totl;",
        "    unrelated line zzz",
        "    var total = Compute(a, b);",
        "    return nothing;",
        "    qqq",
    };

    private const string TypoSnippet = "var total = Compute(a, b);\nreturn total;";

    private const string TwiceSource = "a();\nx();\nb();\nc();\nx();\nd();";

    private static SourceText Text(string source) => SourceText.From(source);

    // The lines inside each fenced block of a message, in order.
    private static List<string[]> Fences(string message)
    {
        var blocks = new List<string[]>();
        List<string>? current = null;
        foreach (var line in message.Split('\n'))
        {
            var isFence = line.Length >= 3 && line.All(c => c == '`');
            if (isFence && current == null)
            {
                current = new List<string>();
            }
            else if (isFence)
            {
                blocks.Add(current!.ToArray());
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        return blocks;
    }

    [Test]
    public void ResolveOrThrow_NoMatchWithCandidates_ThrowsNotFoundListingTheFilesOwnCandidates()
    {
        var source = string.Join("\n", TypoFile);

        var ex = Assert.Throws<ToolNotFoundException>(() => SnippetLookup.ResolveOrThrow(Text(source), TypoSnippet));

        Assert.That(ex!.Message, Does.Contain("contextSnippet was not found"));
        Assert.That(ex.Message, Does.Contain("Window 1 (starts at line 1, score "));
        Assert.That(ex.Message, Does.Contain("    var total = Compute(a, b);\n    return totl;"));
    }

    [Test]
    public void ResolveOrThrow_SnippetLabel_AppearsInTheMessage()
    {
        var source = string.Join("\n", TypoFile);

        var ex = Assert.Throws<ToolNotFoundException>(
            () => SnippetLookup.ResolveOrThrow(Text(source), TypoSnippet, snippetLabel: "oldContent"));

        Assert.That(ex!.Message, Does.Contain("oldContent was not found"));
    }

    [Test]
    public void TryResolve_Hit_ReturnsTheMatchAndNoError()
    {
        const string source = "class C\n{\n    void M()\n    {\n        var a = 1;\n    }\n}";

        var found = SnippetLookup.TryResolve(Text(source), "var a = 1;", null, null, out var match, out var error);

        Assert.That(found, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(match, Is.Not.Null);
        Assert.That(match!.Start, Is.EqualTo(source.IndexOf("var a = 1;", StringComparison.Ordinal)));
        Assert.That(match.Line, Is.EqualTo(5));
        Assert.That(match.Length, Is.EqualTo("var a = 1;".Length));
    }

    [Test]
    public void TryResolve_ResultOverload_ExposesTheOutcomeAndNeverTheCandidatesAsMatches()
    {
        var source = string.Join("\n", TypoFile);

        var found = SnippetLookup.TryResolve(Text(source), TypoSnippet, null, null, out var result);

        Assert.That(found, Is.False);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
        Assert.That(result.Matches, Is.Empty, "a near-match must never be offered as a match");
        Assert.That(result.ClosestWindows, Is.Not.Empty, "the candidates stay available to a caller that wants them");
    }

    [TestCase("var a = 1;")]
    [TestCase("VAR A = 1;")]
    [TestCase("var     a   =    1;")]
    public void TryResolve_CaseAndIndentationAndSpacingTolerantHit(string snippet)
    {
        const string source = "class C\n{\n    void M()\n    {\n        Var A = 1;\n    }\n}";

        var found = SnippetLookup.TryResolve(Text(source), snippet, null, null, out var match, out var error);

        Assert.That(found, Is.True, error);
        Assert.That(match!.Line, Is.EqualTo(5));
        Assert.That(match.Tier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
        Assert.That(source.Substring(match.Start, 3), Is.EqualTo("Var"));
    }

    [Test]
    public void TryResolve_Ambiguous_ReturnsFalseAndListsEveryCandidate()
    {
        var found = SnippetLookup.TryResolve(Text(TwiceSource), "x();", null, null, out var match, out var error);

        Assert.That(found, Is.False);
        Assert.That(match, Is.Null);
        Assert.That(error, Does.Contain("contextSnippet is ambiguous: it matches 2 places"));
        Assert.That(error, Does.Contain("Candidate 1 of 2 (line 2)"));
        Assert.That(error, Does.Contain("Candidate 2 of 2 (line 5)"));
    }

    [Test]
    public void ResolveOrThrow_Ambiguous_ThrowsTheAmbiguousExceptionType()
    {
        Assert.Throws<ToolAmbiguousMatchException>(() => SnippetLookup.ResolveOrThrow(Text(TwiceSource), "x();"));
    }

    [Test]
    public void TryResolve_AmbiguousNarrowedByLineBefore_ResolvesToTheSecondOccurrence()
    {
        var found = SnippetLookup.TryResolve(Text(TwiceSource), "x();", "c();", null, out var match, out var error);

        Assert.That(found, Is.True, error);
        Assert.That(match!.Line, Is.EqualTo(5));
    }

    [Test]
    public void TryResolve_NoMatchWithNothingClose_SaysThereAreNoCandidates()
    {
        var found = SnippetLookup.TryResolve(
            Text("class C\n{\n}"), "qqqq zzzz\nwwww jjjj", null, null, out var match, out var error);

        Assert.That(found, Is.False);
        Assert.That(match, Is.Null);
        Assert.That(error, Does.Contain("No candidates: no window of the file reaches a similarity of 0.50"));
        Assert.That(Fences(error!), Is.Empty);
    }

    [Test]
    public void ResolveOrThrow_NoMatchWithNothingClose_ThrowsNotFoundWithNoWindows()
    {
        var ex = Assert.Throws<ToolNotFoundException>(
            () => SnippetLookup.ResolveOrThrow(Text("class C\n{\n}"), "qqqq zzzz\nwwww jjjj"));

        Assert.That(ex!.Message, Does.Contain("No candidates"));
        Assert.That(Fences(ex.Message), Is.Empty);
    }

    [Test]
    public void ListedCandidate_RoundTripsToASingleMatch()
    {
        var source = string.Join("\n", TypoFile);
        SnippetLookup.TryResolve(Text(source), TypoSnippet, null, null, out _, out var error);

        var firstWindow = Fences(error!)[0];
        var found = SnippetLookup.TryResolve(
            Text(source), string.Join("\n", firstWindow), null, null, out var match, out var again);

        Assert.That(found, Is.True, again);
        Assert.That(match!.Line, Is.EqualTo(1));
    }

    [Test]
    public void ResolveOrThrow_BlankSnippet_ThrowsNotFoundRatherThanMatchingEverywhere()
    {
        Assert.Throws<ToolNotFoundException>(() => SnippetLookup.ResolveOrThrow(Text("a();\nb();"), "   "));
    }
}
