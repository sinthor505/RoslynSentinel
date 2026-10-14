using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Smoke test that the public snippet-matching surface is usable from an assembly that has no
/// InternalsVisibleTo to RoslynSentinel.Common (only RoslynSentinel.Tests has one). It compiles only
/// if SnippetResolver.Resolve and every result type it exposes are public, which is what the engine
/// and tool assemblies will depend on. Matcher behaviour is covered in RoslynSentinel.Tests.
/// </summary>
[TestFixture]
[Category("SnippetResolver")]
public class SnippetResolverCrossAssemblyTests
{
    private const string Source = "class C\n{\n    void M()\n    {\n        var x = 1;\n    }\n}\n";

    [Test]
    public void Resolve_VerbatimSnippet_ReturnsOneExactMatch()
    {
        const string snippet = "var x = 1;";
        var sourceText = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            sourceText, snippet, WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(result.IsSingleMatch, Is.True);
        Assert.That(result.HasNearMatches, Is.False);
        var match = result.Single();
        Assert.That(match.Tier, Is.EqualTo(WhitespaceTolerance.Exact));
        Assert.That(match.Start, Is.EqualTo(Source.IndexOf(snippet, StringComparison.Ordinal)));
        Assert.That(match.Length, Is.EqualTo(snippet.Length));
        Assert.That(match.Line, Is.EqualTo(5));
    }

    [Test]
    public void Resolve_IndentationDroppedSnippetAtExact_ReportsLooserTierNearMatch()
    {
        // The braces and the statement are at column 0 in the snippet but indented in the file, the shape
        // that made ReplaceSnippet fail with a bare NotFound in the Qwen run.
        const string snippet = "{\nvar x = 1;\n}";
        var sourceText = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            sourceText, snippet, WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.IsSingleMatch, Is.False);
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.HasNearMatches, Is.True);
        var near = result.NearMatches[0];
        Assert.That(near.LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
        Assert.That(near.StartLine, Is.EqualTo(4));
        Assert.That(near.ActualLines, Is.EqualTo(new[] { "    {", "        var x = 1;", "    }" }));
    }

    [Test]
    public void Resolve_IndentationDroppedSnippetAtIgnoreIndentation_ReturnsOneMatch()
    {
        const string snippet = "{\nvar x = 1;\n}";
        var sourceText = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            sourceText, snippet, WhitespaceTolerance.IgnoreIndentation, SnippetCaseMode.CaseSensitive);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(result.TryGetSingle(out var match), Is.True);
        Assert.That(match.Tier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
        Assert.That(match.Line, Is.EqualTo(4));
        Assert.That(result.HasNearMatches, Is.False);
    }
}
