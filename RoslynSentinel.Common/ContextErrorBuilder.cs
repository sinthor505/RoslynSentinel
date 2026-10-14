using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

public class ContextErrorBuilder
{
    /// <summary>Cap on candidates listed in a StillAmbiguous message, to keep it small.</summary>
    internal const int MaxStillAmbiguousCandidatesListed = 5;

    /// <summary>
    /// Builds a <see cref="ToolException"/> for a failed contextSnippet match: wording and
    /// exception-type choice for every <see cref="SnippetMatchOutcome"/>, centralized here instead
    /// of each throw site hand-rolling its own message.
    /// </summary>
    public static ToolException Build(
        SnippetMatchOutcome outcome, string contextSnippet, SourceText sourceText,
        List<ContextHelper.SnippetMatch>? matches = null, string? diagnosis = null,
        string? parameterName = null, string? invalidValue = null, bool? isAfter = null,
        bool verbatimRequired = false, string snippetLabel = "contextSnippet",
        Func<int, string?>? memberNameAt = null, bool selectorsSupported = false)
    {
        switch (outcome)
        {
            case SnippetMatchOutcome.NoMatch:
                return BuildNoMatch(contextSnippet, verbatimRequired, diagnosis, snippetLabel);

            case SnippetMatchOutcome.MatchedLooserThanRequested:
                // Reported as NotFound for now: the snippet matched only at a looser tier than the
                // caller accepts. Its dedicated error code and near-match listing arrive in slice 3.
                return BuildNoMatch(contextSnippet, verbatimRequired, diagnosis, snippetLabel);

            case SnippetMatchOutcome.Matched:
                throw new InvalidOperationException(
                    "SnippetMatchOutcome.Matched is a success outcome; there is no error to build for it.");

            case SnippetMatchOutcome.Ambiguous:
                return BuildAmbiguous(contextSnippet, sourceText, matches, stillAmbiguous: false, snippetLabel: snippetLabel, memberNameAt: memberNameAt, selectorsSupported: selectorsSupported);

            case SnippetMatchOutcome.StillAmbiguous:
            case SnippetMatchOutcome.TrueTie:
                return BuildAmbiguous(contextSnippet, sourceText, matches, stillAmbiguous: true, snippetLabel: snippetLabel, memberNameAt: memberNameAt, selectorsSupported: selectorsSupported);

            case SnippetMatchOutcome.SelectorMismatch:
                return BuildSelectorMismatch(contextSnippet, diagnosis, snippetLabel);

            case SnippetMatchOutcome.AdjacentLineMismatch:
                return BuildAdjacentLineMismatch(contextSnippet, diagnosis, snippetLabel);

            case SnippetMatchOutcome.InvalidDisambiguator:
                return BuildInvalidDisambiguator(parameterName, invalidValue, isAfter);

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled SnippetMatchOutcome.");
        }
    }

    private static ToolNotFoundException BuildNoMatch(string contextSnippet, bool verbatimRequired, string? diagnosis, string snippetLabel)
    {
        var trimmed = contextSnippet.Trim();
        var message = verbatimRequired
            ? $"An exact match could not be located for the provided {snippetLabel} (verbatim required): \"{trimmed}\". " +
              "Re-read the file and copy oldContent exactly (including whitespace) from the current content - " +
              "approximate/retyped text is not accepted here."
            : $"An exact match could not be located for the provided {snippetLabel}: \"{trimmed}\".";

        if (!string.IsNullOrEmpty(diagnosis))
        {
            message += "\n" + diagnosis;
        }

        return new ToolNotFoundException(message);
    }

    private static ToolAmbiguousMatchException BuildAmbiguous(
        string contextSnippet, SourceText sourceText, List<ContextHelper.SnippetMatch>? matches, bool stillAmbiguous, string snippetLabel,
        Func<int, string?>? memberNameAt = null, bool selectorsSupported = false)
    {
        var trimmed = contextSnippet.Trim();
        var count = matches?.Count ?? 0;

        if (!stillAmbiguous)
        {
            var candidateText = matches != null
                ? ContextHelper.DescribeAmbiguousCandidates(sourceText, matches, snippetLabel, memberNameAt, selectorsSupported: selectorsSupported)
                : string.Empty;
            return new ToolAmbiguousMatchException(
                $"{snippetLabel} is ambiguous ({count} matches): \"{trimmed}\". " +
                (selectorsSupported
                    ? "Provide matchLine (and withinMember where listed) copied verbatim from one of the matches below " +
                      "to select the intended match; lineBefore/lineAfter may be added as an extra filter:\n"
                    : "Provide lineBefore and/or lineAfter using one of the exact values below (copy " +
                      "verbatim, do not retype from memory) to select the intended match:\n") + candidateText);
        }

        var stillMessage =
            $"{snippetLabel} is still ambiguous ({count} matches remain): \"{trimmed}\". " +
            (selectorsSupported
                ? "Pass matchLine (and withinMember where listed) copied from the candidates below, " +
                  $"provide more specific lineBefore and/or lineAfter content, or widen {snippetLabel}."
                : "Provide more specific lineBefore and/or lineAfter content.");
        if (matches is { Count: > 0 })
        {
            stillMessage += "\nRemaining candidates:\n" + ContextHelper.DescribeAmbiguousCandidates(
                sourceText, matches, snippetLabel, memberNameAt, MaxStillAmbiguousCandidatesListed, selectorsSupported);
        }

        return new ToolAmbiguousMatchException(stillMessage);
    }

    /// <summary>
    /// withinMember/matchLine selected none of the verbatim text matches. The text itself DID match,
    /// so unlike <see cref="BuildNoMatch"/> this never tells the caller to re-read the file and
    /// retype oldContent; the diagnosis lists the current matches with copyable selectors instead.
    /// </summary>
    private static ToolNotFoundException BuildSelectorMismatch(string contextSnippet, string? diagnosis, string snippetLabel)
    {
        var message = $"{snippetLabel} was found, but not at the withinMember/matchLine you supplied: \"{contextSnippet.Trim()}\".";
        if (!string.IsNullOrEmpty(diagnosis))
        {
            message += "\n" + diagnosis;
        }

        return new ToolNotFoundException(message);
    }

    /// <summary>
    /// The text matched, but the lineBefore/lineAfter supplied filtered out every match. Like
    /// <see cref="BuildSelectorMismatch"/>, never tells the caller to re-read the file and retype
    /// oldContent; the diagnosis explains the adjacent-line rule and lists the current matches.
    /// </summary>
    private static ToolNotFoundException BuildAdjacentLineMismatch(string contextSnippet, string? diagnosis, string snippetLabel)
    {
        var message = $"{snippetLabel} was found, but not next to the lineBefore/lineAfter you supplied: \"{contextSnippet.Trim()}\".";
        if (!string.IsNullOrEmpty(diagnosis))
        {
            message += "\n" + diagnosis;
        }

        return new ToolNotFoundException(message);
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
