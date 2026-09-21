using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

public class ContextErrorBuilder
{
    /// <summary>
    /// Builds a <see cref="ToolException"/> for a failed contextSnippet match: wording and
    /// exception-type choice for every <see cref="SnippetMatchOutcome"/>, centralized here instead
    /// of each throw site hand-rolling its own message.
    /// </summary>
    public static ToolException Build(
        SnippetMatchOutcome outcome, string contextSnippet, SourceText sourceText,
        List<ContextHelper.SnippetMatch>? matches = null, string? diagnosis = null,
        string? parameterName = null, string? invalidValue = null, bool? isAfter = null,
        bool verbatimRequired = false)
    {
        switch (outcome)
        {
            case SnippetMatchOutcome.NoMatch:
                return BuildNoMatch(contextSnippet, verbatimRequired, diagnosis);

            case SnippetMatchOutcome.Ambiguous:
                return BuildAmbiguous(contextSnippet, sourceText, matches, stillAmbiguous: false);

            case SnippetMatchOutcome.StillAmbiguous:
            case SnippetMatchOutcome.TrueTie:
                return BuildAmbiguous(contextSnippet, sourceText, matches, stillAmbiguous: true);

            case SnippetMatchOutcome.InvalidDisambiguator:
                return BuildInvalidDisambiguator(parameterName, invalidValue, isAfter);

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled SnippetMatchOutcome.");
        }
    }

    private static ToolNotFoundException BuildNoMatch(string contextSnippet, bool verbatimRequired, string? diagnosis)
    {
        var trimmed = contextSnippet.Trim();
        var message = verbatimRequired
            ? $"An exact match could not be located for the provided contextSnippet (verbatim required): \"{trimmed}\". " +
              "Re-read the file and copy oldContent exactly (including whitespace) from the current content - " +
              "approximate/retyped text is not accepted here."
            : $"An exact match could not be located for the provided contextSnippet: \"{trimmed}\".";

        if (!string.IsNullOrEmpty(diagnosis))
        {
            message += "\n" + diagnosis;
        }

        return new ToolNotFoundException(message);
    }

    private static ToolAmbiguousMatchException BuildAmbiguous(
        string contextSnippet, SourceText sourceText, List<ContextHelper.SnippetMatch>? matches, bool stillAmbiguous)
    {
        var trimmed = contextSnippet.Trim();
        var count = matches?.Count ?? 0;

        if (!stillAmbiguous)
        {
            var candidateText = matches != null
                ? ContextHelper.DescribeAmbiguousCandidates(sourceText, matches)
                : string.Empty;
            return new ToolAmbiguousMatchException(
                $"contextSnippet is ambiguous ({count} matches): \"{trimmed}\". " +
                "Provide lineBefore and/or lineAfter using one of the exact values below (copy " +
                "verbatim, do not retype from memory) to select the intended match:\n" + candidateText);
        }

        return new ToolAmbiguousMatchException(
            $"contextSnippet is still ambiguous ({count} matches remain): \"{trimmed}\". " +
            "Provide more specific lineBefore and/or lineAfter content.");
    }

    private static ToolNotFoundException BuildInvalidDisambiguator(string? parameterName, string? invalidValue, bool? isAfter)
    {
        var direction = isAfter == true ? "after" : "before";
        return new ToolNotFoundException(
            $"Invalid argument: {parameterName} must be a single line, but the supplied value spans " +
            $"multiple lines: \"{invalidValue?.Trim()}\". Pass only the one real source line immediately " +
            $"{direction} the match (e.g. the nearest distinguishing line, not a multi-line block).");
    }
}

/// <summary>
/// Classifies why a contextSnippet-based lookup failed to resolve to exactly one match, so
/// <see cref="ContextErrorBuilder"/> can pick the right exception type and wording instead of
/// each call site hand-rolling its own message. Scoped narrowly to context-snippet/text-anchor
/// errors only -> does not introduce a parallel <see cref="ToolErrorCode"/> taxonomy.
/// </summary>
public enum SnippetMatchOutcome
{
    /// <summary>Zero candidates at all.</summary>
    NoMatch,

    /// <summary>2+ candidates, no lineBefore/lineAfter supplied yet.</summary>
    Ambiguous,

    /// <summary>2+ candidates remain after lineBefore/lineAfter filtering.</summary>
    StillAmbiguous,

    /// <summary>2+ candidates share identical lineBefore AND lineAfter.</summary>
    TrueTie,

    /// <summary>lineBefore/lineAfter itself is malformed (e.g. multi-line).</summary>
    InvalidDisambiguator,
}
