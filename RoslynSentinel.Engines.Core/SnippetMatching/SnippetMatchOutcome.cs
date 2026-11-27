namespace RoslynSentinel.Common;

/// <summary>
/// Classifies the result of a contextSnippet-based lookup: either the lookup resolved
/// (<see cref="Matched"/>) or why it failed to resolve to exactly one match, so
/// the shared error builder can pick the right exception type and wording instead of
/// each call site hand-rolling its own message. Scoped narrowly to context-snippet/text-anchor
/// errors only -> does not introduce a parallel <see cref="ToolErrorCode"/> taxonomy.
/// <see cref="Matched"/> is the one non-error member: the error builder rejects it.
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

    /// <summary>oldContent matched verbatim, but withinMember/matchLine selected none of the matches.</summary>
    SelectorMismatch,

    /// <summary>oldContent matched verbatim, but the lineBefore/lineAfter supplied filtered out every match.</summary>
    AdjacentLineMismatch,

    /// <summary>Success: at least one match at or stricter than the requested whitespace tolerance. Not an error; never passed to the error builder.</summary>
    Matched,

    /// <summary>The snippet matched only at a looser whitespace tolerance than the caller accepts (near-matches only, no accepted match).</summary>
    MatchedLooserThanRequested,
}