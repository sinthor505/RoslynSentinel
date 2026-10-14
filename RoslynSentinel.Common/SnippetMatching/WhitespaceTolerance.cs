namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// How much whitespace difference a snippet match accepts between the snippet and the file text.
/// An ordered ladder: each tier accepts everything the tier before it accepts, so the numeric
/// order of the members is part of the contract (a dispatcher runs matchers in ascending order
/// up to the requested tier and never silently matches at a looser one). Internal use only; no
/// tool schema exposes it, and there is deliberately no default: every caller states its tier.
/// </summary>
internal enum WhitespaceTolerance
{
    /// <summary>Accepts nothing extra: ordinal comparison, no end-of-line normalization.</summary>
    Exact = 0,

    /// <summary>
    /// Also accepts CR, LF and CRLF as interchangeable between the snippet and the file.
    /// Everything else (indentation, trailing whitespace, interior runs) must match as written.
    /// </summary>
    IgnoreLineEndings = 1,

    /// <summary>
    /// Also accepts differing leading AND trailing whitespace on each line. Interior text is still
    /// compared as written. The reported span excludes the first line's indentation, consistent
    /// with the exact matcher, so a replacement does not eat the file's indentation.
    /// </summary>
    IgnoreIndentation = 2,

    /// <summary>
    /// Also accepts any whitespace difference: per-line trim plus collapse of interior whitespace
    /// runs. The loosest tier, intended for read and lookup callers.
    /// </summary>
    IgnoreAllWhitespace = 3,
}

/// <summary>
/// Whether a snippet match compares letter case. A separate axis from <see cref="WhitespaceTolerance"/>,
/// not a rung on that ladder: case is not whitespace, and C# is case-sensitive, so a case-insensitive
/// hit can resolve to a different identifier. There is no default: lookup callers state
/// <see cref="CaseInsensitive"/> and edit callers state <see cref="CaseSensitive"/> explicitly.
/// </summary>
internal enum SnippetCaseMode
{
    /// <summary>Ordinal comparison; "Foo" and "foo" are different text.</summary>
    CaseSensitive = 0,

    /// <summary>Ordinal-ignore-case comparison; "Foo" and "foo" match.</summary>
    CaseInsensitive = 1,
}
