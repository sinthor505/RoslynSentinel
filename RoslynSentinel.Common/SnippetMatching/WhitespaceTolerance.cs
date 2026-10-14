namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// How much whitespace difference a snippet match accepts between the snippet and the file text.
/// An ordered ladder: each tier accepts everything the tier before it accepts, so the numeric
/// order of the members is part of the contract (a dispatcher runs matchers in ascending order
/// up to the requested tier and never silently matches at a looser one). Public so that callers in the
/// engine and tool assemblies can state a tier, but not exposed in any tool schema. There is deliberately
/// no default: every caller states its tier.
/// <para>Contract: <see cref="SnippetResolver.Resolve"/> returns matches at or stricter than the requested
/// tier (a stricter match is acceptable: an <see cref="IgnoreIndentation"/> request against a file that
/// matches verbatim succeeds). A snippet that matches only at a looser tier is reported as near-matches,
/// which are never edit candidates. The dispatcher stops at the first tier that finds a match, so an
/// <see cref="Exact"/> request never silently matches at a looser tier.</para>
/// </summary>
public enum WhitespaceTolerance
{
    /// <summary>
    /// Accepts nothing extra: case-sensitive ordinal comparison, no end-of-line normalization, so a multi-line
    /// snippet must carry the file's own line endings. Every occurrence is reported (overlapping ones included)
    /// and each span's length is the snippet's own length, which is the true length because the comparison is
    /// ordinal.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Also accepts CR, LF and CRLF as interchangeable between the snippet and the file.
    /// Everything else (indentation, trailing whitespace, interior runs) must match as written. Case-sensitive.
    /// Only a multi-line snippet can differ from <see cref="Exact"/>; for a single-line snippet the two tiers
    /// behave identically. The span is the real match in the file, so its length can differ from the snippet's
    /// when the file's line endings differ from the snippet's.
    /// </summary>
    IgnoreLineEndings = 1,

    /// <summary>
    /// Also accepts differing leading AND trailing whitespace on each line. Interior text is still
    /// compared as written. The reported span excludes the first line's indentation, consistent
    /// with the exact matcher, so a replacement does not eat the file's indentation. Case-sensitive.
    /// <para>Semantics are fragment at the edges and whole-line equality inside, so everything
    /// <see cref="IgnoreLineEndings"/> accepts is accepted here too. A single-line snippet is a trimmed
    /// substring match inside a source line. For a multi-line snippet (fully blank leading and trailing snippet
    /// lines are dropped) the first line must be a suffix of the source line's trim-end text, each interior line
    /// must equal its source line after trimming both ends, and the last line must be a prefix of its source
    /// line's trim-start text. The span is the real span in the file, never the snippet's length.</para>
    /// </summary>
    IgnoreIndentation = 2,

    /// <summary>
    /// Also accepts any whitespace difference: per-line trim plus collapse of interior whitespace
    /// runs. The loosest tier, intended for read and lookup callers; the only tier that accepts
    /// <see cref="SnippetCaseMode.CaseInsensitive"/>.
    /// <para>A single-line stage (the collapsed snippet as a substring of each collapsed source line) runs first;
    /// only if it finds nothing and the snippet is multi-line does a window stage compare the snippet against
    /// windows of the same number of consecutive source lines. Each span is the real span in the file, never the
    /// snippet's length. Span conventions differ between the two stages: the single-line stage starts at the
    /// matched fragment (after the line's indentation), whereas the window stage starts at the first line's start
    /// (so the span includes that line's indentation) and ends at the last line's end without its line break.
    /// Replacing a window span with caller text therefore drops the file's indentation unless the caller supplied
    /// it.</para>
    /// </summary>
    IgnoreAllWhitespace = 3,
}

/// <summary>
/// Whether a snippet match compares letter case. A separate axis from <see cref="WhitespaceTolerance"/>,
/// not a rung on that ladder: case is not whitespace, and C# is case-sensitive, so a case-insensitive
/// hit can resolve to a different identifier. There is no default: lookup callers state
/// <see cref="CaseInsensitive"/> and edit callers state <see cref="CaseSensitive"/> explicitly.
/// <para>Restriction: <see cref="CaseInsensitive"/> is accepted only together with
/// <see cref="WhitespaceTolerance.IgnoreAllWhitespace"/>, the one tier whose stages can compare case-insensitively
/// today. <see cref="SnippetResolver.Resolve"/> throws <see cref="ArgumentException"/> for any other combination (a
/// programming error, not a match outcome). Under that combination the ordinal and line-ending stages still compare
/// case-sensitively.</para>
/// </summary>
public enum SnippetCaseMode
{
    /// <summary>Ordinal comparison; "Foo" and "foo" are different text.</summary>
    CaseSensitive = 0,

    /// <summary>Ordinal-ignore-case comparison; "Foo" and "foo" match.</summary>
    CaseInsensitive = 1,
}
