using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// The lookup-caller policy of the whitespace-tolerance proposal, as one-liners: read and lookup tools (the ones that
/// locate a node, token or symbol from a contextSnippet, as opposed to the edit tools that splice text) resolve with
/// <see cref="WhitespaceTolerance.IgnoreAllWhitespace"/> and <see cref="SnippetCaseMode.CaseInsensitive"/>, the loosest
/// tier. Every method here runs <see cref="SnippetResolver"/> with exactly that request.
/// <list type="bullet">
/// <item>A lookup never receives a best-guess near-match as a result: only <see cref="SnippetMatchOutcome.Matched"/>
/// yields a <see cref="SnippetMatch"/>. Nothing in <c>NearMatches</c> or <c>ClosestWindows</c> is ever returned as a
/// match (at the loosest tier a looser-tier near-match cannot exist anyway).</item>
/// <item>A failure lists candidates: the throwing form and the error string of the Try form are built by
/// <see cref="SnippetErrorFormatter.Build"/>, so they carry the file's own candidate lines, the tiers probed and the
/// echoed snippet. A caller that wants the candidates as data uses <see cref="Resolve"/> or the result overload of
/// <see cref="TryResolve(SourceText, string, string?, string?, out SnippetMatchResult)"/>.</item>
/// <item>Several matches are <see cref="SnippetMatchOutcome.Ambiguous"/> (or <c>StillAmbiguous</c> once lineBefore or
/// lineAfter ran), a failure; no form here picks the first of several.</item>
/// </list>
/// Only lineBefore and lineAfter are offered: no lookup caller has withinMember or matchLine. A caller that needs a
/// different tier, case mode or selectors calls <see cref="SnippetResolver"/> directly.
/// </summary>
public static class SnippetLookup
{
    /// <summary>The snippet parameter name used in error wording when the caller does not name its own.</summary>
    public const string DefaultSnippetLabel = "contextSnippet";

    /// <summary>
    /// Resolves <paramref name="snippet"/> under the lookup policy and returns the outcome as data. Never throws for a
    /// match outcome (no match, several matches, a malformed lineBefore/lineAfter, a blank snippet).
    /// </summary>
    /// <param name="sourceText">The text to search.</param>
    /// <param name="snippet">The snippet to find.</param>
    /// <param name="lineBefore">Optional single line before the match, to narrow several matches; null means not supplied.</param>
    /// <param name="lineAfter">Optional single line after the match, to narrow several matches; null means not supplied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceText"/> or <paramref name="snippet"/> is null.</exception>
    public static SnippetMatchResult Resolve(
        SourceText sourceText, string snippet, string? lineBefore = null, string? lineAfter = null)
        => SnippetResolver.Resolve(
            sourceText, snippet, WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive,
            new SnippetSelectors(LineBefore: lineBefore, LineAfter: lineAfter), memberNameAt: null);

    /// <summary>
    /// Like <see cref="Resolve"/> but returns whether the snippet resolved to exactly one match. The result is always
    /// returned, so a caller that wants the candidates of a failure can use them. Never throws for a match outcome.
    /// </summary>
    public static bool TryResolve(
        SourceText sourceText, string snippet, string? lineBefore, string? lineAfter, out SnippetMatchResult result)
    {
        result = Resolve(sourceText, snippet, lineBefore, lineAfter);
        return result.Outcome == SnippetMatchOutcome.Matched;
    }

    /// <summary>
    /// Like <see cref="Resolve"/> but returns the single match, or false with <paramref name="error"/> set to the
    /// candidate-listing message <see cref="SnippetErrorFormatter"/> builds for the failure. Never throws for a match
    /// outcome; <paramref name="match"/> is null exactly when the result is false.
    /// </summary>
    /// <param name="snippetLabel">The caller's parameter name for the snippet, used in the error wording.</param>
    public static bool TryResolve(
        SourceText sourceText, string snippet, string? lineBefore, string? lineAfter,
        [NotNullWhen(true)] out SnippetMatch? match, out string? error, string snippetLabel = DefaultSnippetLabel)
    {
        var result = Resolve(sourceText, snippet, lineBefore, lineAfter);
        if (result.Outcome == SnippetMatchOutcome.Matched)
        {
            match = result.Matches.Single();
            error = null;
            return true;
        }

        match = null;
        error = SnippetErrorFormatter.Build(result, sourceText, snippet, snippetLabel).Message;
        return false;
    }

    /// <summary>
    /// Like <see cref="Resolve"/> but returns the single match or throws. Any outcome other than
    /// <see cref="SnippetMatchOutcome.Matched"/> throws the <see cref="ToolException"/> built by
    /// <see cref="SnippetErrorFormatter.Build"/> (<see cref="ToolNotFoundException"/> for no match,
    /// <see cref="ToolAmbiguousMatchException"/> for several), whose message lists the candidates and the tiers probed.
    /// </summary>
    /// <param name="snippetLabel">The caller's parameter name for the snippet, used in the error wording.</param>
    public static SnippetMatch ResolveOrThrow(
        SourceText sourceText, string snippet, string? lineBefore = null, string? lineAfter = null,
        string snippetLabel = DefaultSnippetLabel)
    {
        var result = Resolve(sourceText, snippet, lineBefore, lineAfter);
        if (result.Outcome != SnippetMatchOutcome.Matched)
        {
            throw SnippetErrorFormatter.Build(result, sourceText, snippet, snippetLabel);
        }

        return result.Matches.Single();
    }
}
